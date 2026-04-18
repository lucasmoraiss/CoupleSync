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

namespace CoupleSync.IntegrationTests.Goals;

public sealed class GoalsIntegrationTests
{
    private static CreateGoalRequestDto ValidCreateRequest() => new(
        Title: "Vacation Fund",
        Description: "Save for a trip",
        TargetAmount: 1000m,
        Currency: "BRL",
        Deadline: DateTime.UtcNow.AddDays(90));

    // ── POST /goals ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoal_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateGoal_AuthenticatedButNoCoupleId_ShouldReturn403()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-goal-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateGoal_WithValidInput_ShouldReturn201()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"create-goal-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoalResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Vacation Fund", payload!.Title);
        Assert.Equal("Active", payload.Status);
    }

    [Fact]
    public async Task CreateGoal_WithEmptyTitle_ShouldReturn400()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"bad-goal-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/goals",
            ValidCreateRequest() with { Title = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── GET /goals ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoals_WithNoGoals_ShouldReturnEmptyList()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"empty-goals-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/goals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetGoalsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0, payload!.TotalCount);
        Assert.Empty(payload.Items);
    }

    [Fact]
    public async Task GetGoals_AfterCreation_ShouldReturnGoals()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"list-goals-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var response = await client.GetAsync("/api/v1/goals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetGoalsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.TotalCount);
        Assert.Single(payload.Items);
    }

    [Fact]
    public async Task GetGoals_CoupleScopedIsolation_ShouldNotReturnOtherCouplesGoals()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A creates a goal
        var emailA = $"couple-a-goals-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        // Couple B queries goals — should see none
        var emailB = $"couple-b-goals-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.GetAsync("/api/v1/goals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetGoalsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(0, payload!.TotalCount);
    }

    // ── GET /goals/{id} ────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoalById_WhenGoalExists_ShouldReturn200()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"get-by-id-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        var response = await client.GetAsync($"/api/v1/goals/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoalResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(created.Id, payload!.Id);
    }

    [Fact]
    public async Task GetGoalById_NotFound_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"get-missing-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/v1/goals/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGoalById_CrossCouple_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A creates a goal
        var emailA = $"couple-a-get-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        // Couple B tries to fetch Couple A's goal
        var emailB = $"couple-b-get-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.GetAsync($"/api/v1/goals/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /goals/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGoal_WithValidInput_ShouldReturn200()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"patch-goal-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/goals/{created!.Id}",
            new UpdateGoalRequestDto(Title: "New Title", Description: null, TargetAmount: null, Deadline: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoalResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("New Title", payload!.Title);
    }

    [Fact]
    public async Task UpdateGoal_NotFound_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"patch-missing-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/goals/{Guid.NewGuid()}",
            new UpdateGoalRequestDto(Title: "X", Description: null, TargetAmount: null, Deadline: null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── DELETE /goals/{id}/archive ─────────────────────────────────────────

    [Fact]
    public async Task ArchiveGoal_WithValidGoal_ShouldReturn200WithArchivedStatus()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"archive-goal-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        var response = await client.DeleteAsync($"/api/v1/goals/{created!.Id}/archive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoalResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Archived", payload!.Status);
    }

    [Fact]
    public async Task ArchiveGoal_NotFound_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"archive-missing-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.DeleteAsync($"/api/v1/goals/{Guid.NewGuid()}/archive");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET /goals/{id}/progress ───────────────────────────────────────────

    [Fact]
    public async Task GetGoalProgress_NewGoal_ShouldReturn200WithZeroContributions()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"progress-new-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        var response = await client.GetAsync($"/api/v1/goals/{created!.Id}/progress");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoalProgressResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(created.Id, payload!.GoalId);
        Assert.Equal("Vacation Fund", payload.Title);
        Assert.Equal(1000m, payload.TargetAmount);
        Assert.Equal(0m, payload.ContributedAmount);
        Assert.Equal(0m, payload.ProgressPercent);
        Assert.False(payload.IsAchieved);
        Assert.True(payload.DaysRemaining > 0);
        Assert.Equal("Active", payload.Status);
    }

    [Fact]
    public async Task GetGoalProgress_NotFound_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"progress-missing-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/v1/goals/{Guid.NewGuid()}/progress");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGoalProgress_CrossCouple_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A creates a goal
        var emailA = $"progress-couple-a-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        // Couple B tries to fetch progress for Couple A's goal
        var emailB = $"progress-couple-b-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.GetAsync($"/api/v1/goals/{created!.Id}/progress");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGoalProgress_ArchivedGoal_ShouldReturnArchivedStatus()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"progress-archived-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsJsonAsync("/api/v1/goals", ValidCreateRequest());
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        var archiveResp = await client.DeleteAsync($"/api/v1/goals/{created!.Id}/archive");
        Assert.Equal(HttpStatusCode.OK, archiveResp.StatusCode);

        var response = await client.GetAsync($"/api/v1/goals/{created.Id}/progress");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoalProgressResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Archived", payload!.Status);
    }

    [Fact]
    public async Task GetGoalProgress_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/goals/{Guid.NewGuid()}/progress");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LinkTransactionToGoal_WhenValid_ProgressEndpointReturnsContributedAmount()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"link-txn-goal-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create a goal with target 500 BRL
        var createGoalResp = await client.PostAsJsonAsync("/api/v1/goals", new CreateGoalRequestDto(
            Title: "Viagem",
            Description: null,
            TargetAmount: 500m,
            Currency: "BRL",
            Deadline: DateTime.UtcNow.AddDays(90)));
        Assert.Equal(HttpStatusCode.Created, createGoalResp.StatusCode);
        var created = await createGoalResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        // Ingest a transaction worth 100 BRL
        var ingestResp = await client.PostAsJsonAsync("/api/v1/integrations/events", new
        {
            Bank = "NUBANK",
            Amount = 100m,
            Currency = "BRL",
            EventTimestamp = DateTime.UtcNow.AddMinutes(-5),
            Description = "Test purchase",
            Merchant = "Test Store",
            RawNotificationText = "Nubank: R$100,00 no Test Store"
        });
        Assert.Equal(HttpStatusCode.Created, ingestResp.StatusCode);

        // Fetch the transaction list to get the ID
        var listResp = await client.GetAsync("/api/v1/transactions");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(list);
        Assert.True(list.Items.Count > 0);
        var transactionId = list.Items[0].Id;

        // Link the transaction to the goal
        var linkResp = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{transactionId}/goal",
            new { GoalId = created!.Id });
        Assert.Equal(HttpStatusCode.NoContent, linkResp.StatusCode);

        // Verify progress endpoint returns ContributedAmount > 0
        var progressResp = await client.GetAsync($"/api/v1/goals/{created.Id}/progress");
        Assert.Equal(HttpStatusCode.OK, progressResp.StatusCode);
        var progress = await progressResp.Content.ReadFromJsonAsync<GoalProgressResponseDto>();
        Assert.NotNull(progress);
        Assert.Equal(100m, progress.ContributedAmount);
        Assert.True(progress.ProgressPercent > 0m);
    }

    [Fact]
    public async Task LinkTransactionToGoal_CrossCoupleGoal_ShouldReturn404()
    {
        await using var factory = new GoalsWebApplicationFactory();
        using var client = factory.CreateClient();

        var emailA = $"link-cross-a-{Guid.NewGuid():N}@example.com";
        var emailB = $"link-cross-b-{Guid.NewGuid():N}@example.com";

        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);

        // Couple A creates a goal
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var createGoalResp = await client.PostAsJsonAsync("/api/v1/goals", new CreateGoalRequestDto(
            Title: "Goal A",
            Description: null,
            TargetAmount: 200m,
            Currency: "BRL",
            Deadline: DateTime.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, createGoalResp.StatusCode);
        var goalA = await createGoalResp.Content.ReadFromJsonAsync<GoalResponseDto>();

        // Couple B ingests a transaction
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var ingestResp = await client.PostAsJsonAsync("/api/v1/integrations/events", new
        {
            Bank = "NUBANK",
            Amount = 50m,
            Currency = "BRL",
            EventTimestamp = DateTime.UtcNow.AddMinutes(-5),
            Description = "Test",
            Merchant = "Store",
            RawNotificationText = "Nubank: R$50,00 no Store"
        });
        Assert.Equal(HttpStatusCode.Created, ingestResp.StatusCode);

        var listResp = await client.GetAsync("/api/v1/transactions");
        var list = await listResp.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        var transactionId = list!.Items[0].Id;

        // Couple B tries to link its transaction to Couple A's goal
        var linkResp = await client.PatchAsJsonAsync(
            $"/api/v1/transactions/{transactionId}/goal",
            new { GoalId = goalA!.Id });
        Assert.Equal(HttpStatusCode.NotFound, linkResp.StatusCode);

        // Verify the transaction's GoalId was not modified by the failed link attempt
        var verifyListResp = await client.GetAsync("/api/v1/transactions");
        Assert.Equal(HttpStatusCode.OK, verifyListResp.StatusCode);
        var verifyList = await verifyListResp.Content.ReadFromJsonAsync<GetTransactionsResponseDto>();
        Assert.NotNull(verifyList);
        Assert.NotEmpty(verifyList!.Items);
        Assert.Null(verifyList.Items[0].GoalId);
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

    private sealed record CreateGoalRequestDto(
        string Title,
        string? Description,
        decimal TargetAmount,
        string? Currency,
        DateTime Deadline);

    private sealed record UpdateGoalRequestDto(
        string? Title,
        string? Description,
        decimal? TargetAmount,
        DateTime? Deadline);

    private sealed record GoalResponseDto(
        Guid Id,
        Guid CreatedByUserId,
        string Title,
        string? Description,
        decimal TargetAmount,
        string Currency,
        DateTime Deadline,
        string Status,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record GetGoalsResponseDto(int TotalCount, IReadOnlyList<GoalResponseDto> Items);

    private sealed record GoalProgressResponseDto(
        Guid GoalId,
        string Title,
        decimal TargetAmount,
        decimal ContributedAmount,
        decimal ProgressPercent,
        bool IsAchieved,
        double DaysRemaining,
        string Status);

    private sealed record TransactionItemDto(
        Guid Id,
        Guid CoupleId,
        Guid UserId,
        string Bank,
        decimal Amount,
        string Currency,
        DateTime EventTimestampUtc,
        string? Description,
        string? Merchant,
        string Category,
        DateTime CreatedAtUtc)
    {
        public Guid? GoalId { get; init; }
    }

    private sealed record GetTransactionsResponseDto(
        int TotalCount, int Page, int PageSize, IReadOnlyList<TransactionItemDto> Items);

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);
}

internal sealed class GoalsWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString =
        $"Data Source=couplesync-goals-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _keepAliveConnection;

    public GoalsWebApplicationFactory()
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
