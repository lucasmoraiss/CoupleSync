using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.IntegrationTests.CashFlow;

public sealed class CashFlowIntegrationTests
{
    [Fact]
    public async Task GetCashFlow_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new CashFlowWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/cashflow?horizon=30");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCashFlow_AuthenticatedNoCoupleId_ShouldReturn403()
    {
        await using var factory = new CashFlowWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"cf-no-couple-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/cashflow?horizon=30");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetCashFlow_Horizon30_WithNoTransactions_ShouldReturn200WithZeroedValues()
    {
        await using var factory = new CashFlowWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cf-zero-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/cashflow?horizon=30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CashFlowResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(30, payload!.Horizon);
        Assert.Equal(0, payload.TransactionCount);
        Assert.Equal(0m, payload.TotalHistoricalSpend);
        Assert.Equal(0m, payload.AverageDailySpend);
        Assert.Equal(0m, payload.ProjectedSpend);
        Assert.NotNull(payload.Assumptions);
        Assert.NotEqual(default, payload.GeneratedAtUtc);
    }

    [Fact]
    public async Task GetCashFlow_Horizon90_WithNoTransactions_ShouldReturn200()
    {
        await using var factory = new CashFlowWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cf-90-zero-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/cashflow?horizon=90");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CashFlowResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(90, payload!.Horizon);
    }

    [Fact]
    public async Task GetCashFlow_InvalidHorizon_ShouldReturn400()
    {
        await using var factory = new CashFlowWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cf-bad-horizon-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/cashflow?horizon=45");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCashFlow_CrossCoupleIsolation_CoupleBCannotSeeCoupleAData()
    {
        await using var factory = new CashFlowWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A registers and ingest a transaction
        var emailA = $"cf-couple-a-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var ingestResp = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest(99.99m));
        Assert.Equal(HttpStatusCode.Created, ingestResp.StatusCode);

        // Couple A can see the transaction in cash flow
        var respA = await client.GetAsync("/api/v1/cashflow?horizon=30");
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var payloadA = await respA.Content.ReadFromJsonAsync<CashFlowResponseDto>();
        Assert.NotNull(payloadA);
        Assert.Equal(1, payloadA!.TransactionCount);

        // Couple B registers and queries cash flow — should see zero transactions
        var emailB = $"cf-couple-b-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var respB = await client.GetAsync("/api/v1/cashflow?horizon=30");
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var payloadB = await respB.Content.ReadFromJsonAsync<CashFlowResponseDto>();
        Assert.NotNull(payloadB);
        Assert.Equal(0, payloadB!.TransactionCount);
        Assert.Equal(0m, payloadB.TotalHistoricalSpend);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IngestRequestDto BuildIngestRequest(decimal amount = 99.99m) => new(
        Bank: "NUBANK",
        Amount: amount,
        Currency: "BRL",
        EventTimestamp: DateTime.UtcNow.AddMinutes(-5),
        Description: "Test purchase",
        Merchant: "Test Store",
        RawNotificationText: "Nubank: R$99,99 at Test Store");

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

    // ── Local DTOs ─────────────────────────────────────────────────────────

    private sealed record CashFlowResponseDto(
        int Horizon,
        DateTime HistoricalPeriodStart,
        DateTime HistoricalPeriodEnd,
        int TransactionCount,
        decimal TotalHistoricalSpend,
        decimal AverageDailySpend,
        decimal ProjectedSpend,
        Dictionary<string, decimal> CategoryBreakdown,
        string Assumptions,
        DateTime GeneratedAtUtc);

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
}

internal sealed class CashFlowWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString =
        $"Data Source=couplesync-cashflow-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _keepAliveConnection;

    public CashFlowWebApplicationFactory()
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
