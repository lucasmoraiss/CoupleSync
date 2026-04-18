using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Persistence.Seeders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.IntegrationTests.Dashboard;

public sealed class DashboardIntegrationTests
{
    [Fact]
    public async Task GetDashboard_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDashboard_AuthenticatedButNoCoupleId_ShouldReturn403()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-dash-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task GetDashboard_WithNoTransactions_ShouldReturnZeroAggregates()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"dash-empty-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetDashboardResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0m, payload!.TotalExpenses);
        Assert.Equal(0, payload.TransactionCount);
        Assert.Empty(payload.ExpensesByCategory);
        Assert.Empty(payload.PartnerBreakdown);
        Assert.NotEqual(default, payload.PeriodStart);
        Assert.NotEqual(default, payload.PeriodEnd);
        Assert.NotEqual(default, payload.GeneratedAtUtc);
    }

    [Fact]
    public async Task GetDashboard_AfterIngest_ShouldReturnCorrectAggregates()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"dash-agg-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ingest = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest(50m, "IFOOD DELIVERY"));
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        var ingest2 = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest(30m, "RANDOM STORE"));
        Assert.Equal(HttpStatusCode.Created, ingest2.StatusCode);

        var response = await client.GetAsync("/api/v1/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetDashboardResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(80m, payload!.TotalExpenses);
        Assert.Equal(2, payload.TransactionCount);
        Assert.NotEmpty(payload.ExpensesByCategory);
        Assert.Single(payload.PartnerBreakdown);
        Assert.Equal(80m, payload.PartnerBreakdown[0].TotalAmount);
    }

    [Fact]
    public async Task GetDashboard_CoupleScopedIsolation_ShouldNotReturnOtherCouplesData()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A ingests a transaction
        var emailA = $"dash-iso-a-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var ingestA = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest(200m));
        Assert.Equal(HttpStatusCode.Created, ingestA.StatusCode);

        // Couple B queries dashboard — must see only their own (zero) data
        var emailB = $"dash-iso-b-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.GetAsync("/api/v1/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetDashboardResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0m, payload!.TotalExpenses);
        Assert.Equal(0, payload.TransactionCount);
    }

    [Fact]
    public async Task GetDashboard_WithDateRangeFilter_ShouldReturnOnlyMatchingTransactions()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"dash-range-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Ingest a transaction with recent timestamp (within default current-month range)
        await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest(99m));

        // Query with a narrow date range that excludes the ingested transaction
        var futureStart = DateTime.UtcNow.AddMonths(1).ToString("O");
        var futureEnd = DateTime.UtcNow.AddMonths(2).ToString("O");

        var response = await client.GetAsync($"/api/v1/dashboard?startDate={Uri.EscapeDataString(futureStart)}&endDate={Uri.EscapeDataString(futureEnd)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetDashboardResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0, payload!.TransactionCount);
        Assert.Equal(0m, payload.TotalExpenses);
    }

    [Fact]
    public async Task GetDashboard_BothPartners_ShouldSeeSameAggregates()
    {
        await using var factory = new DashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        // Partner A registers and creates couple
        var emailA = $"dash-both-a-{Guid.NewGuid():N}@example.com";
        const string password = "SecurePass123!";

        var regA = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = emailA, Name = "Partner A", Password = password });
        regA.EnsureSuccessStatusCode();
        var authA = await regA.Content.ReadFromJsonAsync<AuthResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authA!.AccessToken);
        var createCouple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCouple.StatusCode);

        // Get couple join code
        var coupleResp = await client.GetAsync("/api/v1/couples/me");
        var coupleData = await coupleResp.Content.ReadFromJsonAsync<CoupleResponseDto>();

        // Re-login partner A to get token with couple_id
        client.DefaultRequestHeaders.Authorization = null;
        var loginA = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = emailA, Password = password });
        loginA.EnsureSuccessStatusCode();
        var loginAuthA = await loginA.Content.ReadFromJsonAsync<AuthResponseDto>();
        var tokenA = loginAuthA!.AccessToken;

        // Partner A ingests a transaction
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest(150m));

        // Partner B registers and joins the couple
        var emailB = $"dash-both-b-{Guid.NewGuid():N}@example.com";
        client.DefaultRequestHeaders.Authorization = null;
        var regB = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = emailB, Name = "Partner B", Password = password });
        regB.EnsureSuccessStatusCode();
        var authB = await regB.Content.ReadFromJsonAsync<AuthResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authB!.AccessToken);
        var joinResp = await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = coupleData!.JoinCode });
        Assert.Equal(HttpStatusCode.OK, joinResp.StatusCode);

        // Re-login partner B to get token with couple_id
        client.DefaultRequestHeaders.Authorization = null;
        var loginB = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = emailB, Password = password });
        loginB.EnsureSuccessStatusCode();
        var loginAuthB = await loginB.Content.ReadFromJsonAsync<AuthResponseDto>();
        var tokenB = loginAuthB!.AccessToken;

        // Both partners query dashboard and should get the same TotalExpenses
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var responseA = await client.GetAsync("/api/v1/dashboard");
        var dashA = await responseA.Content.ReadFromJsonAsync<GetDashboardResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var responseB = await client.GetAsync("/api/v1/dashboard");
        var dashB = await responseB.Content.ReadFromJsonAsync<GetDashboardResponseDto>();

        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
        Assert.NotNull(dashA);
        Assert.NotNull(dashB);
        Assert.Equal(dashA!.TotalExpenses, dashB!.TotalExpenses);
        Assert.Equal(dashA.TransactionCount, dashB.TransactionCount);
    }

    private static IngestRequestDto BuildIngestRequest(decimal amount = 99.99m, string merchant = "Test Store")
    {
        return new IngestRequestDto(
            Bank: "NUBANK",
            Amount: amount,
            Currency: "BRL",
            EventTimestamp: DateTime.UtcNow.AddMinutes(-5),
            Description: "Test purchase",
            Merchant: merchant,
            RawNotificationText: $"Nubank: R${amount:F2} at {merchant}");
    }

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = "SecurePass123!"
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        return auth!.AccessToken;
    }

    private static async Task<string> RegisterWithCoupleAndGetTokenAsync(HttpClient client, string email)
    {
        const string password = "SecurePass123!";

        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = password
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registerAuth!.AccessToken);

        var createCoupleResponse = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCoupleResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return loginAuth!.AccessToken;
    }

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);
    private sealed record AuthUserDto(Guid Id, string Email, string Name);
    private sealed record IngestRequestDto(
        string Bank,
        decimal Amount,
        string Currency,
        DateTime EventTimestamp,
        string? Description,
        string? Merchant,
        string? RawNotificationText);
    private sealed record PartnerBreakdownDto(Guid UserId, decimal TotalAmount);
    private sealed record GetDashboardResponseDto(
        decimal TotalExpenses,
        Dictionary<string, decimal> ExpensesByCategory,
        List<PartnerBreakdownDto> PartnerBreakdown,
        int TransactionCount,
        DateTime PeriodStart,
        DateTime PeriodEnd,
        DateTime GeneratedAtUtc);
    private sealed record CoupleResponseDto(Guid Id, string JoinCode, List<MemberDto> Members);
    private sealed record MemberDto(Guid UserId, string Role);
    private sealed record ErrorDto(string Code, string Message, string TraceId);
}

internal sealed class DashboardWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString = $"Data Source=couplesync-dash-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAliveConnection;

    public DashboardWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience
            };
            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();

            _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
            _keepAliveConnection.Open();

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite(_databaseConnectionString);
            });

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();

            var seeder = new CategoryRulesSeeder(db);
            seeder.SeedAsync().GetAwaiter().GetResult();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
        Environment.SetEnvironmentVariable("JWT__SECRET", null);
        Environment.SetEnvironmentVariable("JWT__ISSUER", null);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
    }
}
