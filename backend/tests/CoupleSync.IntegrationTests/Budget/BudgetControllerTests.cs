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

namespace CoupleSync.IntegrationTests.Budget;

[Trait("Category", "Budget")]
public sealed class BudgetControllerTests
{
    private static string CurrentMonth() => $"{DateTime.UtcNow:yyyy-MM}";

    private static CreateBudgetPlanRequestDto ValidPlanRequest() => new(
        Month: CurrentMonth(),
        GrossIncome: 6000m,
        Currency: "BRL");

    private static ReplaceAllocationsRequestDto ValidAllocationsRequest() => new(
        Allocations:
        [
            new("Food", 1000m, "BRL"),
            new("Transport", 500m, "BRL"),
            new("Bills", 1500m, "BRL")
        ]);

    // ── Unauthenticated / no couple ────────────────────────────────────────

    [Fact]
    public async Task BudgetEndpoints_WithoutAuth_Returns401()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BudgetEndpoints_WithoutCouple_Returns403()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-budget-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── POST /api/v1/budgets ───────────────────────────────────────────────

    [Fact]
    public async Task UpsertBudgetPlan_ValidRequest_ReturnsOk()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"upsert-budget-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(CurrentMonth(), payload!.Month);
        Assert.Equal(6000m, payload.GrossIncome);
        Assert.Equal("BRL", payload.Currency);
        Assert.NotEqual(Guid.Empty, payload.Id);
    }

    [Fact]
    public async Task UpsertBudgetPlan_Upsert_UpdatesExistingPlan()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"upsert-update-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // First create
        var firstResponse = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var firstPayload = await firstResponse.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(firstPayload);

        // Upsert with updated gross income
        var secondResponse = await client.PostAsJsonAsync("/api/v1/budgets",
            ValidPlanRequest() with { GrossIncome = 9000m });

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var secondPayload = await secondResponse.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(secondPayload);
        Assert.Equal(9000m, secondPayload!.GrossIncome);
        Assert.Equal(firstPayload!.Id, secondPayload.Id); // same plan id — updated, not created new
    }

    // ── GET /api/v1/budgets/current ────────────────────────────────────────

    [Fact]
    public async Task GetCurrentBudgetPlan_NoPlan_ReturnsNotFound()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"no-plan-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/budgets/current");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCurrentBudgetPlan_WithPlan_ReturnsOk()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"current-plan-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);

        var response = await client.GetAsync("/api/v1/budgets/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(CurrentMonth(), payload!.Month);
    }

    // ── GET /api/v1/budgets/{month} ────────────────────────────────────────

    [Fact]
    public async Task GetBudgetPlanByMonth_WithPlan_ReturnsOk()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"by-month-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var month = CurrentMonth();
        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest() with { Month = month });
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);

        var response = await client.GetAsync($"/api/v1/budgets/{month}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(month, payload!.Month);
    }

    // ── PUT /api/v1/budgets/{planId}/allocations ───────────────────────────

    [Fact]
    public async Task ReplaceAllocations_ValidRequest_ReturnsOk()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"replace-alloc-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var plan = await upsert.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(plan);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/budgets/{plan!.Id}/allocations",
            ValidAllocationsRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Allocations.Count);
        Assert.Contains(payload.Allocations, a => a.Category == "Food" && a.AllocatedAmount == 1000m);
    }

    [Fact]
    public async Task ReplaceAllocations_MoreThan20_Returns422()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"too-many-alloc-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var plan = await upsert.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(plan);

        var tooMany = new ReplaceAllocationsRequestDto(
            Allocations: Enumerable.Range(1, 21)
                .Select(i => new AllocationItemDto($"Category{i}", 100m, "BRL"))
                .ToArray());

        var response = await client.PutAsJsonAsync(
            $"/api/v1/budgets/{plan!.Id}/allocations",
            tooMany);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(error);
        Assert.Equal("BUDGET_ALLOCATION_LIMIT", error!.Code);
    }

    [Fact]
    public async Task ReplaceAllocations_DuplicateCategory_Returns422()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"dup-cat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var plan = await upsert.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(plan);

        var dupRequest = new ReplaceAllocationsRequestDto(
            Allocations:
            [
                new("Food", 500m, "BRL"),
                new("Food", 300m, "BRL") // duplicate
            ]);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/budgets/{plan!.Id}/allocations",
            dupRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(error);
        Assert.Equal("BUDGET_ALLOCATION_DUPLICATE_CATEGORY", error!.Code);
    }

    [Fact]
    public async Task ReplaceAllocations_CurrencyMismatch_Returns422()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"currency-mismatch-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest()); // plan in BRL
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var plan = await upsert.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(plan);

        var mismatchRequest = new ReplaceAllocationsRequestDto(
            Allocations:
            [
                new("Food", 500m, "USD") // USD mismatch against BRL plan
            ]);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/budgets/{plan!.Id}/allocations",
            mismatchRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(error);
        Assert.Equal("BUDGET_ALLOCATION_CURRENCY_MISMATCH", error!.Code);
    }

    // ── Couple isolation ───────────────────────────────────────────────────

    [Fact]
    public async Task BudgetEndpoints_DifferentCouple_ReturnsNotFound()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A creates a plan
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, $"couple-a-iso-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var planA = await upsert.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(planA);

        // Couple B tries to replace allocations for Couple A's plan
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, $"couple-b-iso-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/budgets/{planA!.Id}/allocations",
            ValidAllocationsRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/v1/budgets/income ──────────────────────────────────────

    [Fact]
    [Trait("Category", "QuickIncome")]
    public async Task UpdateIncome_ValidRequest_ReturnsOk()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"quick-income-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync("/api/v1/budgets/income", new { grossIncome = 7500m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<UpdateIncomeResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(7500m, payload!.GrossIncome);
        Assert.Equal("BRL", payload.Currency);
        Assert.Equal(CurrentMonth(), payload.Month);
        Assert.NotEqual(Guid.Empty, payload.PlanId);
    }

    [Fact]
    [Trait("Category", "QuickIncome")]
    public async Task UpdateIncome_AutoCreatesPlan_WhenNoPlanExists()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"quick-income-new-plan-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // No plan created beforehand
        var response = await client.PatchAsJsonAsync("/api/v1/budgets/income", new { grossIncome = 4000m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<UpdateIncomeResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(4000m, payload!.GrossIncome);

        // Verify the plan exists and has no allocations
        var planResponse = await client.GetAsync("/api/v1/budgets/current");
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        var plan = await planResponse.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(plan);
        Assert.Empty(plan!.Allocations);
    }

    [Fact]
    [Trait("Category", "QuickIncome")]
    public async Task UpdateIncome_PreservesExistingAllocations()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"quick-income-preserve-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create plan with allocations
        var upsert = await client.PostAsJsonAsync("/api/v1/budgets", ValidPlanRequest());
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var plan = await upsert.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(plan);

        await client.PutAsJsonAsync($"/api/v1/budgets/{plan!.Id}/allocations", ValidAllocationsRequest());

        // Update income only
        var response = await client.PatchAsJsonAsync("/api/v1/budgets/income", new { grossIncome = 9000m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify allocations preserved
        var planAfter = await client.GetAsync("/api/v1/budgets/current");
        var planAfterDto = await planAfter.Content.ReadFromJsonAsync<BudgetPlanResponseDto>();
        Assert.NotNull(planAfterDto);
        Assert.Equal(9000m, planAfterDto!.GrossIncome);
        Assert.Equal(3, planAfterDto.Allocations.Count);
    }

    [Fact]
    [Trait("Category", "QuickIncome")]
    public async Task UpdateIncome_GrossIncomeZero_Returns400()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"quick-income-zero-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync("/api/v1/budgets/income", new { grossIncome = 0m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "QuickIncome")]
    public async Task UpdateIncome_Unauthenticated_Returns401()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/v1/budgets/income", new { grossIncome = 5000m });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "QuickIncome")]
    public async Task UpdateIncome_WithoutCouple_Returns403()
    {
        await using var factory = new BudgetWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"quick-income-nocouple-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync("/api/v1/budgets/income", new { grossIncome = 5000m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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

    private sealed record CreateBudgetPlanRequestDto(string Month, decimal GrossIncome, string Currency);

    private sealed record AllocationItemDto(string Category, decimal AllocatedAmount, string Currency);

    private sealed record ReplaceAllocationsRequestDto(IReadOnlyList<AllocationItemDto> Allocations);

    private sealed record BudgetAllocationResponseDto(
        Guid Id,
        string Category,
        decimal AllocatedAmount,
        string Currency,
        decimal ActualSpent,
        decimal Remaining);

    private sealed record BudgetPlanResponseDto(
        Guid Id,
        string Month,
        decimal GrossIncome,
        string Currency,
        IReadOnlyList<BudgetAllocationResponseDto> Allocations,
        decimal BudgetGap,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record ErrorDto(string Code, string Message, string TraceId);

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);

    private sealed record UpdateIncomeResponseDto(Guid PlanId, string Month, decimal GrossIncome, string Currency);
}

internal sealed class BudgetWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString =
        $"Data Source=couplesync-budget-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAliveConnection;

    public BudgetWebApplicationFactory()
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
