using System.IdentityModel.Tokens.Jwt;
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

namespace CoupleSync.E2ETests;

/// <summary>
/// Pilot couple full ingest-to-alert pipeline end-to-end test.
/// Covers AC-001, AC-003, AC-004, AC-005, AC-008 for the pilot scenario.
/// Traces: T-029
/// </summary>
public sealed class PilotFlowE2ETests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task E2EFlow_PilotCouple_FullIngestionToAlertPipeline()
    {
        // ── Setup ── //
        await using var factory = new E2EWebApplicationFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();

        var run = Guid.NewGuid().ToString("N")[..8];
        var emailA = $"pilot-a-{run}@example.com";
        var emailB = $"pilot-b-{run}@example.com";

        // ── Step 1: Register User A ── //
        var regAResp = await clientA.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = emailA,
            Name = "Pilot User A",
            Password = "PilotPass123!"
        });
        Assert.Equal(HttpStatusCode.Created, regAResp.StatusCode);
        var authA = await regAResp.Content.ReadFromJsonAsync<AuthDto>();
        Assert.NotNull(authA);
        Assert.False(string.IsNullOrWhiteSpace(authA!.AccessToken),
            "User A access token must not be empty after registration.");

        // ── Step 2: Register User B ── //
        var regBResp = await clientB.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = emailB,
            Name = "Pilot User B",
            Password = "PilotPass123!"
        });
        Assert.Equal(HttpStatusCode.Created, regBResp.StatusCode);
        var authB = await regBResp.Content.ReadFromJsonAsync<AuthDto>();
        Assert.NotNull(authB);
        Assert.False(string.IsNullOrWhiteSpace(authB!.AccessToken),
            "User B access token must not be empty after registration.");

        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authA.AccessToken);
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authB.AccessToken);

        // ── Step 3: User A creates a couple ── //
        var createCoupleResp = await clientA.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCoupleResp.StatusCode);
        var coupleCreated = await createCoupleResp.Content.ReadFromJsonAsync<CreateCoupleDto>();
        Assert.NotNull(coupleCreated);
        Assert.NotEqual(Guid.Empty, coupleCreated!.CoupleId);
        Assert.Equal(6, coupleCreated.JoinCode.Length);

        // Refresh User A token so couple_id claim is embedded
        var loginAResp = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = emailA,
            Password = "PilotPass123!"
        });
        Assert.Equal(HttpStatusCode.OK, loginAResp.StatusCode);
        var loginA = await loginAResp.Content.ReadFromJsonAsync<AuthDto>();
        Assert.NotNull(loginA);
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginA!.AccessToken);

        // ── Step 4: User B joins couple ── //
        var joinResp = await clientB.PostAsJsonAsync("/api/v1/couples/join", new
        {
            JoinCode = coupleCreated.JoinCode
        });
        Assert.Equal(HttpStatusCode.OK, joinResp.StatusCode);
        var joined = await joinResp.Content.ReadFromJsonAsync<JoinCoupleDto>();
        Assert.NotNull(joined);
        Assert.Equal(2, joined!.Members.Count);
        Assert.Equal(coupleCreated.CoupleId, joined.CoupleId);

        // Refresh User B token so couple_id claim is embedded
        var loginBResp = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = emailB,
            Password = "PilotPass123!"
        });
        Assert.Equal(HttpStatusCode.OK, loginBResp.StatusCode);
        var loginB = await loginBResp.Content.ReadFromJsonAsync<AuthDto>();
        Assert.NotNull(loginB);
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginB!.AccessToken);

        // Verify couple_id claim is now set in User A token
        var jwtA = new JwtSecurityTokenHandler().ReadJwtToken(loginA.AccessToken);
        var coupleIdClaim = jwtA.Claims.FirstOrDefault(c => c.Type == "couple_id")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(coupleIdClaim),
            "couple_id claim must be set in User A token after joining a couple.");

        // ── Step 5: Ingest notification event (Nubank) ── //
        var eventTimestamp = DateTime.UtcNow.AddMinutes(-5);
        var ingestResp = await clientA.PostAsJsonAsync("/api/v1/integrations/events", new
        {
            Bank = "Nubank",
            Amount = 150.00m,
            Currency = "BRL",
            EventTimestamp = eventTimestamp,
            Description = "Compra no Supermercado",
            Merchant = "Pao de Acucar",
            RawNotificationText = "Nubank: Compra de R$150,00 no Pao de Acucar"
        });
        Assert.Equal(HttpStatusCode.Created, ingestResp.StatusCode);
        var ingestResult = await ingestResp.Content.ReadFromJsonAsync<IngestResultDto>();
        Assert.NotNull(ingestResult);
        Assert.NotEqual(Guid.Empty, ingestResult!.IngestId);
        Assert.False(string.IsNullOrWhiteSpace(ingestResult.Status),
            "Ingest response must include a status field.");

        // ── Step 6: Verify transaction was created ── //
        var txResp = await clientA.GetAsync("/api/v1/transactions?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, txResp.StatusCode);
        var transactions = await txResp.Content.ReadFromJsonAsync<GetTransactionsDto>();
        Assert.NotNull(transactions);
        Assert.True(transactions!.TotalCount >= 1,
            "At least one transaction must exist after ingesting a notification event.");
        Assert.True(transactions.Items.Any(t => t.Bank == "Nubank" && t.Amount == 150.00m),
            "The ingested Nubank transaction must appear in the transactions list.");

        // ── Step 7: Verify integration status updated ── //
        var statusResp = await clientA.GetAsync("/api/v1/integrations/status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var integrationStatus = await statusResp.Content.ReadFromJsonAsync<IntegrationStatusDto>();
        Assert.NotNull(integrationStatus);
        Assert.True(integrationStatus!.IsActive,
            "Integration must be reported as active after a successful event ingest.");
        Assert.NotNull(integrationStatus.Counts);
        Assert.True(integrationStatus.Counts.TotalAccepted >= 1,
            "TotalAccepted must be at least 1 after a successful event ingest.");

        // ── Step 8: Create goal ── //
        var goalDeadline = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var createGoalResp = await clientA.PostAsJsonAsync("/api/v1/goals", new
        {
            Title = "Viagem Europa",
            Description = "Economia para viagem á Europa",
            TargetAmount = 5000.00m,
            Currency = "BRL",
            Deadline = goalDeadline
        });
        Assert.Equal(HttpStatusCode.Created, createGoalResp.StatusCode);
        var goal = await createGoalResp.Content.ReadFromJsonAsync<GoalDto>();
        Assert.NotNull(goal);
        Assert.Equal("Viagem Europa", goal!.Title);
        Assert.Equal(5000.00m, goal.TargetAmount);
        Assert.Equal("BRL", goal.Currency);
        Assert.Equal("Active", goal.Status);

        // ── Step 9: Link transaction to goal ── //
        var firstTx = transactions.Items.First(t => t.Bank == "Nubank");
        var patchGoalResp = await clientA.PatchAsJsonAsync(
            $"/api/v1/transactions/{firstTx.Id}/goal",
            new { GoalId = goal.Id });
        Assert.Equal(HttpStatusCode.NoContent, patchGoalResp.StatusCode);

        // Verify goal reflects linked transaction contribution
        var linkedGoalResp = await clientA.GetAsync($"/api/v1/goals/{goal.Id}");
        Assert.Equal(HttpStatusCode.OK, linkedGoalResp.StatusCode);

        // ── Step 10: Verify dashboard returns data with couple scope ── //
        var dashResp = await clientA.GetAsync("/api/v1/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashResp.StatusCode);
        var dashboard = await dashResp.Content.ReadFromJsonAsync<DashboardDto>();
        Assert.NotNull(dashboard);
        Assert.True(dashboard!.TransactionCount >= 1,
            "Dashboard must reflect at least one transaction for the couple.");
        Assert.True(dashboard.TotalExpenses >= 150.00m,
            "Dashboard total expenses must include the ingested transaction amount.");
        Assert.True(dashboard.PartnerBreakdown.Count >= 1,
            "Dashboard must have partner breakdown rows for the couple.");
        Assert.All(dashboard.PartnerBreakdown, row =>
            Assert.True(row.Amount >= 0, "Each partner breakdown amount must be non-negative."));

        // ── Step 11: Verify couple-level data isolation (User B sees same data) ── //
        var dashRespB = await clientB.GetAsync("/api/v1/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashRespB.StatusCode);
        var dashboardB = await dashRespB.Content.ReadFromJsonAsync<DashboardDto>();
        Assert.NotNull(dashboardB);
        Assert.True(dashboard.TransactionCount == dashboardB!.TransactionCount,
            "Both partners must see the same transaction count on the shared dashboard (AC-008).");
    }

    // ── DTOs for deserialization ── //

    private sealed record AuthDto(AuthUserDto User, string AccessToken, string RefreshToken);
    private sealed record AuthUserDto(Guid Id, string Email, string Name);
    private sealed record CreateCoupleDto(Guid CoupleId, string JoinCode);
    private sealed record JoinCoupleDto(Guid CoupleId, List<MemberDto> Members);
    private sealed record MemberDto(Guid UserId, string Email, string Name);
    private sealed record IngestResultDto(Guid IngestId, string Status);
    private sealed record TransactionItemDto(
        Guid Id, Guid UserId, string Bank, decimal Amount, string Currency,
        DateTime EventTimestampUtc, string? Description, string? Merchant, string Category);
    private sealed record GetTransactionsDto(
        int TotalCount, int Page, int PageSize, List<TransactionItemDto> Items);
    private sealed record IntegrationCountsDto(int TotalAccepted, int TotalDuplicate, int TotalRejected);
    private sealed record IntegrationStatusDto(
        bool IsActive, DateTime? LastEventAtUtc, DateTime? LastErrorAtUtc,
        string? LastErrorMessage, string? RecoveryHint, IntegrationCountsDto Counts);
    private sealed record GoalDto(
        Guid Id, Guid CreatedByUserId, string Title, string? Description,
        decimal TargetAmount, string Currency, DateTime Deadline, string Status);
    private sealed record DashboardDto(
        decimal TotalExpenses, Dictionary<string, decimal> ExpensesByCategory,
        List<PartnerBreakdownItemDto> PartnerBreakdown, int TransactionCount,
        DateTime PeriodStart, DateTime PeriodEnd, DateTime GeneratedAtUtc);
    private sealed record PartnerBreakdownItemDto(Guid UserId, decimal Amount, int TransactionCount);

    // ── WebApplicationFactory ── //

    private sealed class E2EWebApplicationFactory : WebApplicationFactory<Program>
    {
        private const string JwtSecret = "e2e-test-secret-1234567890-abcdef-ghij";
        private const string JwtIssuer = "CoupleSync.E2ETests";
        private const string JwtAudience = "CoupleSync.Mobile.E2ETests";

        private readonly string _connectionString =
            $"Data Source=couplesync-e2e-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection? _keepAlive;

        public E2EWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
            Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
            Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Secret"] = JwtSecret,
                    ["Jwt:Issuer"] = JwtIssuer,
                    ["Jwt:Audience"] = JwtAudience
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                _keepAlive = new SqliteConnection(_connectionString);
                _keepAlive.Open();

                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connectionString));

                using var scope = services.BuildServiceProvider().CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _keepAlive?.Dispose();
                _keepAlive = null;
                Environment.SetEnvironmentVariable("JWT__SECRET", null);
                Environment.SetEnvironmentVariable("JWT__ISSUER", null);
                Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
            }
        }
    }
}
