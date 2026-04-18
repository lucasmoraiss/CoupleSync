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

namespace CoupleSync.IntegrationTests.Transactions;

public sealed class TransactionIntegrationTests
{
    [Fact]
    public async Task GetTransactions_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTransactions_AuthenticatedButNoCoupleId_ShouldReturn403()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task GetTransactions_WithNoIngested_ShouldReturnEmptyPage()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"empty-txn-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0, payload!.TotalCount);
        Assert.Empty(payload.Items);
        Assert.Equal(1, payload.Page);
        Assert.Equal(20, payload.PageSize);
    }

    [Fact]
    public async Task GetTransactions_AfterIngest_ShouldReturnIngestedTransactions()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"txn-list-{Guid.NewGuid():N}@example.com";
        var token = await RegisterWithCoupleAndGetTokenAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ingestResponse = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest());
        Assert.Equal(HttpStatusCode.Created, ingestResponse.StatusCode);
        var ingest = await ingestResponse.Content.ReadFromJsonAsync<IngestResponseDto>();
        Assert.Equal("Accepted", ingest!.Status);

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.TotalCount);
        Assert.Single(payload.Items);
        Assert.Equal(99.99m, payload.Items[0].Amount);
        Assert.Equal("BRL", payload.Items[0].Currency);
        Assert.Equal("NUBANK", payload.Items[0].Bank);
    }

    [Fact]
    public async Task GetTransactions_CoupleScopedIsolation_ShouldNotReturnOtherCouplesTransactions()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A ingests a transaction
        var emailA = $"couple-a-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var ingestA = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest());
        Assert.Equal(HttpStatusCode.Created, ingestA.StatusCode);

        // Couple B registers and queries transactions — must see only their own (zero)
        var emailB = $"couple-b-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0, payload!.TotalCount);
    }

    [Fact]
    public async Task GetTransactions_WithCategoryFilter_ShouldReturnOnlyMatchingCategory()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"cat-filter-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Ingest iFood transaction (maps to Alimentação)
        await client.PostAsJsonAsync("/api/v1/integrations/events",
            BuildIngestRequest() with { Merchant = "IFOOD DELIVERY" });

        // Ingest unknown merchant (maps to OUTROS)
        await client.PostAsJsonAsync("/api/v1/integrations/events",
            BuildIngestRequest(Math.Round((decimal)(new Random().NextDouble() * 100) + 1, 2)));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transactions = await db.Transactions.IgnoreQueryFilters().OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
        Assert.True(transactions.Count >= 2);

        var alimentacaoCategory = transactions.First(t => t.Merchant == "IFOOD DELIVERY").Category;

        var response = await client.GetAsync($"/api/v1/transactions?category={Uri.EscapeDataString(alimentacaoCategory)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(payload);
        Assert.All(payload!.Items, item => Assert.Equal(alimentacaoCategory, item.Category));
    }

    [Fact]
    public async Task PatchCategory_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{Guid.NewGuid()}/category",
            new { Category = "Alimentação" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_AuthenticatedButNoCoupleId_ShouldReturn403()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-patch-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{Guid.NewGuid()}/category",
            new { Category = "Alimentação" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_WithNonExistentTransaction_ShouldReturn404()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"patch-notfound-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{Guid.NewGuid()}/category",
            new { Category = "Alimentação" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_WithOtherCouplesTransaction_ShouldReturn404()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A ingests a transaction
        var emailA = $"couple-a-patch-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var ingestResp = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest());
        Assert.Equal(HttpStatusCode.Created, ingestResp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var txn = await db.Transactions.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAtUtc).FirstAsync();

        // Couple B tries to patch Couple A's transaction
        var emailB = $"couple-b-patch-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{txn.Id}/category",
            new { Category = "Alimentação" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_WithEmptyCategory_ShouldReturn400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"patch-empty-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{Guid.NewGuid()}/category",
            new { Category = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_WithCategoryExceeding64Chars_ShouldReturn400()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"patch-long-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var longCategory = new string('A', 65);
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{Guid.NewGuid()}/category",
            new { Category = longCategory });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCategory_WithValidCategoryAndExistingTransaction_ShouldReturn200WithUpdatedCategory()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"patch-ok-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ingestResp = await client.PostAsJsonAsync("/api/v1/integrations/events", BuildIngestRequest());
        Assert.Equal(HttpStatusCode.Created, ingestResp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var txn = await db.Transactions.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAtUtc).FirstAsync();

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{txn.Id}/category",
            new { Category = "Saúde" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<TransactionResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(txn.Id, payload!.Id);
        Assert.Equal("Saúde", payload.Category);
    }

    [Fact]
    public async Task GetTransactions_DefaultPagination_ShouldUseDefaults()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"page-def-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Page);
        Assert.Equal(20, payload.PageSize);
    }

    private static IngestRequestDto BuildIngestRequest(decimal amount = 99.99m)
    {
        return new IngestRequestDto(
            Bank: "NUBANK",
            Amount: amount,
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
    private sealed record TransactionResponseDto(
        Guid Id, Guid CoupleId, Guid UserId, string Bank, decimal Amount,
        string Currency, DateTime EventTimestampUtc, string? Description,
        string? Merchant, string Category, DateTime CreatedAtUtc);
    private sealed record GetTransactionsResponseDto(
        int TotalCount, int Page, int PageSize, List<TransactionResponseDto> Items);
    private sealed record ErrorDto(string Code, string Message, string TraceId);
}

internal sealed class TransactionWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString = $"Data Source=couplesync-txn-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAliveConnection;

    public TransactionWebApplicationFactory()
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
