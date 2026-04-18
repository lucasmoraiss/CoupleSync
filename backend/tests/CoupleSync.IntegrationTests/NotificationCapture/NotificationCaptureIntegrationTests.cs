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

namespace CoupleSync.IntegrationTests.NotificationCapture;

public sealed class NotificationCaptureIntegrationTests
{
    [Fact]
    public async Task IngestEvent_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildValidRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_AuthenticatedButNoCoupleId_ShouldReturn403()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildValidRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task IngestEvent_WithValidPayload_ShouldReturn201WithIngestId()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"ingest-valid-{Guid.NewGuid():N}@example.com";
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginAuth!.AccessToken);

        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.NotNull(payload);
        Assert.NotEqual(Guid.Empty, payload!.IngestId);
        Assert.Equal("Accepted", payload.Status);
    }

    [Fact]
    public async Task IngestEvent_WithNegativeAmount_ShouldReturn400WithValidationError()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"neg-amount-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { Amount = -10m };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithUnsupportedBank_ShouldReturn400WithValidationError()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"unsup-bank-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { Bank = "UNKNOWN_BANK" };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithFutureTimestamp_ShouldReturn400WithValidationError()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"future-ts-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { EventTimestamp = DateTime.UtcNow.AddHours(1) };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithHtmlInDescription_ShouldReturn201()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"html-desc-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { Description = "<script>alert(1)</script>Payment" };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Accepted", payload!.Status);
    }

    [Fact]
    public async Task IngestEvent_SameEventTwice_SecondReturnsDuplicate()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"dedup-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest();

        var response1 = await client.PostAsJsonAsync("/api/v1/integrations/events", request);
        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
        var payload1 = await response1.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.Equal("Accepted", payload1!.Status);

        var response2 = await client.PostAsJsonAsync("/api/v1/integrations/events", request);
        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);
        var payload2 = await response2.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.Equal("Duplicate", payload2!.Status);
    }

    [Fact]
    public async Task IngestEvent_WithIfoodMerchant_AssignsAlimentacaoCategory()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cat-ifood-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { Merchant = "IFOOD DELIVERY" };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.Equal("Accepted", payload!.Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await db.Transactions.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefaultAsync();
        Assert.NotNull(transaction);
        Assert.Equal("Alimentação", transaction!.Category);
    }

    [Fact]
    public async Task IngestEvent_WithUnknownMerchant_AssignsOutrosCategory()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cat-outros-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { Merchant = "TOTALLY UNKNOWN STORE XYZ" };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.Equal("Accepted", payload!.Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await db.Transactions.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefaultAsync();
        Assert.NotNull(transaction);
        Assert.Equal("OUTROS", transaction!.Category);
    }

    [Fact]
    public async Task IngestEvent_UberEats_MatchesAlimentacaoNotTransporte()
    {
        await using var factory = new NotificationCaptureWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cat-ubereats-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = BuildValidRequest() with { Merchant = "UBER EATS delivery" };
        var response = await client.PostAsJsonAsync("/api/v1/integrations/events", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.Equal("Accepted", payload!.Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transaction = await db.Transactions.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefaultAsync();
        Assert.NotNull(transaction);
        Assert.Equal("Alimentação", transaction!.Category);
        Assert.NotEqual("Transporte", transaction.Category);
    }

    private static IngestRequestDto BuildValidRequest()
    {
        return new IngestRequestDto(
            Bank: "NUBANK",
            Amount: 99.99m,
            Currency: "BRL",
            EventTimestamp: DateTime.UtcNow.AddMinutes(-5),
            Description: "Test purchase",
            Merchant: "Test Store",
            RawNotificationText: "Nubank: R$99,99 at Test Store");
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
    private sealed record IngestResponseDto(Guid IngestId, string Status);
    private sealed record ErrorDto(string Code, string Message, string TraceId);
}

internal sealed class NotificationCaptureWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString = $"Data Source=couplesync-ingest-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAliveConnection;

    public NotificationCaptureWebApplicationFactory()
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
