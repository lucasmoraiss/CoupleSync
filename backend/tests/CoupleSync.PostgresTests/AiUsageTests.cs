using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #37 on real PostgreSQL: the migration that adds ai_usage (additive, over existing data), its column types
/// and indexes, and the counters of the chain — budgets, ceilings, pauses and exhausted links — computed by
/// PostgreSQL from the rows, with a gateway built again for every call (nothing in memory). Providers are stubs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AiUsageTests
{
    private const string MigrationBeforeAiUsage = "20261007205425_AddOpenFinanceConnections";
    private const string Gemini = "gemini";
    private const string Flash = "gemini-flash-latest";
    private const string Lite = "gemini-flash-lite-latest";

    private readonly PostgresServer _server;

    public AiUsageTests(PostgresServer server) => _server = server;

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_OnlyAddsAiUsage_AndKeepsEveryExistingRowAndColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBeforeAiUsage);

        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'ai_usage'"));
        var existingTables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        var countsBefore = new Dictionary<string, long>();
        foreach (var table in existingTables) countsBefore[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(countsBefore["transactions"] > 0 && countsBefore["users"] > 0, "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var columnsBefore = await ColumnsOfAsync(database, existingTables);

        await MigrationTests.MigrateAsync(database);

        foreach (var table in existingTables)
            Assert.Equal(countsBefore[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(columnsBefore, await ColumnsOfAsync(database, existingTables));

        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'ai_usage'"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage"));

        await using var db = MigrationTests.Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheMigration_CreatesExactlyTheColumnsAndIndexesOfTheDesign_AndNothingThatCouldHoldAPrompt()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);

        var columns = (await database.RowsAsync(
                "SELECT column_name, data_type, is_nullable, character_maximum_length FROM information_schema.columns WHERE table_name = 'ai_usage'"))
            .ToDictionary(r => (string)r[0]!, r => $"{r[1]}{(r[3] is null ? string.Empty : $"({r[3]})")} {((string)r[2]! == "YES" ? "null" : "not null")}");

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = "uuid not null",
                ["created_at_utc"] = "timestamp with time zone not null",
                ["day_utc"] = "date not null",
                ["day_brt"] = "date not null",
                ["provider"] = "character varying(40) not null",
                ["model"] = "character varying(80) not null",
                ["couple_id"] = "uuid null",
                ["feature"] = "character varying(40) not null",
                ["input_tokens"] = "integer not null",
                ["output_tokens"] = "integer not null",
                ["outcome"] = "character varying(40) not null",
                ["latency_ms"] = "integer not null",
                ["retry_at_utc"] = "timestamp with time zone null",
            }.OrderBy(c => c.Key),
            columns.OrderBy(c => c.Key));

        var indexes = (await database.RowsAsync("SELECT indexdef FROM pg_indexes WHERE tablename = 'ai_usage' ORDER BY indexname"))
            .Select(r => (string)r[0]!)
            .ToList();
        Assert.Equal(3, indexes.Count);
        Assert.Contains(indexes, i => i.Contains("(couple_id, day_brt)"));
        Assert.Contains(indexes, i => i.Contains("(day_utc, provider, model)"));
        Assert.Contains(indexes, i => i.Contains("UNIQUE") && i.Contains("(id)"));

        // Accounting only: no foreign key ties it to a group (there are calls without one).
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.table_constraints WHERE table_name = 'ai_usage' AND constraint_type = 'FOREIGN KEY'"));
    }

    // ---------------------------------------------------------------- the counters, by PostgreSQL

    [PostgresFact]
    public async Task TheGroupBudget_TheGlobalCeiling_AndTheJobCeiling_AreCountedByPostgres_PerBrasiliaDay()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var chain = new Chain(database);
        chain.Options.GroupDailyCalls = 3;
        chain.Options.GlobalDailyInteractiveCalls = 5;
        chain.Options.JobDailyCalls = 2;
        var (groupA, groupB) = (Guid.NewGuid(), Guid.NewGuid());

        // 23:30 in Brasília on the 8th is already the 9th in UTC: the group's day is the Brasília one.
        chain.Clock.UtcNow = new DateTime(2026, 10, 9, 2, 30, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++) Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(groupA));
        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, await chain.AskAsync(groupA));
        Assert.Equal(3, chain.First.Calls);

        // Another group goes on (isolation), until the ceiling of the whole app: 3 + 2 = 5.
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(groupB));
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(groupB));
        Assert.Equal(LlmGatewayOutcome.GlobalBudgetExhausted, await chain.AskAsync(groupB));
        Assert.Equal(LlmGatewayOutcome.GlobalBudgetExhausted, await chain.AskAsync(Guid.NewGuid()));
        Assert.Equal(5, chain.First.Calls);

        // Jobs are outside both, under their own ceiling.
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(groupA, LlmFeatures.InsightWeekly, LlmCallMode.Job));
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null, LlmFeatures.InsightMonthly, LlmCallMode.Job));
        Assert.Equal(LlmGatewayOutcome.GlobalBudgetExhausted, await chain.AskAsync(null, LlmFeatures.InsightWeekly, LlmCallMode.Job));
        Assert.Equal(7, chain.First.Calls);

        // The stored days: Brasília's 8th, UTC's 9th.
        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage WHERE day_brt = DATE '2026-10-08' AND day_utc = DATE '2026-10-09'"));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage WHERE couple_id = @g AND feature = 'chat'", ("g", groupA)));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage WHERE couple_id IS NULL"));

        // Midnight of Brasília (03:00 UTC): everything starts again.
        chain.Clock.UtcNow = new DateTime(2026, 10, 9, 3, 0, 1, DateTimeKind.Utc);
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(groupA));
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null, LlmFeatures.InsightWeekly, LlmCallMode.Job));
    }

    [PostgresFact]
    public async Task TheGroupTokenBudget_IsSummedByPostgres_AndInvalidAnswersCount()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var chain = new Chain(database);
        var group = Guid.NewGuid();
        await using (var db = MigrationTests.Context(database))
        {
            db.AiUsages.Add(AiUsage.Record(chain.Clock.UtcNow, Gemini, Flash, group, LlmFeatures.Chat, 40_000, 19_990, "InvalidOutput", 10));
            db.AiUsages.Add(AiUsage.Record(chain.Clock.UtcNow, Gemini, Flash, group, LlmFeatures.Chat, 0, 0, "RateLimitedMinute", 10));
            db.AiUsages.Add(AiUsage.Record(chain.Clock.UtcNow, Gemini, Flash, Guid.NewGuid(), LlmFeatures.Chat, 50_000, 50_000, "Ok", 10));
            await db.SaveChangesAsync();
        }

        chain.Clock.Advance(TimeSpan.FromMinutes(3)); // past the pause of the seeded 429
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(group)); // 59,990 + 150 tokens
        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, await chain.AskAsync(group));
    }

    [PostgresFact]
    public async Task A429_PausesTheLink_ThenExhaustsIt_UntilTheUtcDayTurns_AllFromTheRows()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var chain = new Chain(database);
        chain.First.Answer = () => Chain.Failed(LlmOutcome.RateLimitedMinute);

        async Task ExpectAsync(int callsOfTheFirstLink)
        {
            Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
            Assert.Equal(callsOfTheFirstLink, chain.First.Calls);
        }

        await ExpectAsync(1);                                   // 429 → 2 minutes
        chain.Clock.Advance(TimeSpan.FromSeconds(100));
        await ExpectAsync(1);
        chain.Clock.Advance(TimeSpan.FromSeconds(30));
        await ExpectAsync(2);                                   // 429 → 10 minutes
        chain.Clock.Advance(TimeSpan.FromMinutes(9));
        await ExpectAsync(2);
        chain.Clock.Advance(TimeSpan.FromMinutes(2));
        await ExpectAsync(3);                                   // 429 → 30 minutes
        chain.Clock.Advance(TimeSpan.FromMinutes(31));
        await ExpectAsync(4);                                   // 4th in a row → out for the day
        chain.Clock.Advance(TimeSpan.FromHours(5));
        await ExpectAsync(4);

        Assert.Equal(
            ["RateLimitedMinute", "RateLimitedMinute", "RateLimitedMinute", "QuotaExhaustedDay"],
            (await database.RowsAsync("SELECT outcome FROM ai_usage WHERE model = 'gemini-flash-latest' ORDER BY created_at_utc")).Select(r => (string)r[0]!));

        // Next UTC day: tried again; an Ok starts the count from zero.
        chain.Clock.UtcNow = new DateTime(2026, 10, 9, 0, 0, 5, DateTimeKind.Utc);
        chain.First.Answer = Chain.Ok;
        await ExpectAsync(5);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage WHERE model = 'gemini-flash-latest' AND outcome = 'Ok'"));
    }

    [PostgresFact]
    public async Task A429WithARetryDelay_KeepsTheLinkOutUntilThatInstant_ReadFromTheRow()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var chain = new Chain(database);
        var start = chain.Clock.UtcNow;

        // The day's quota, back in 77,800 s (the delay of the real answer): beyond the next UTC midnight.
        chain.First.Answer = () => Chain.Failed(LlmOutcome.QuotaExhaustedDay) with { RetryAfter = TimeSpan.FromSeconds(77_800) };
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        Assert.Equal(
            start.AddSeconds(77_800),
            (await database.ScalarAsync<DateTime>("SELECT retry_at_utc FROM ai_usage WHERE outcome = 'QuotaExhaustedDay'")).ToUniversalTime());

        chain.First.Answer = Chain.Ok;
        chain.Clock.UtcNow = new DateTime(2026, 10, 9, 0, 0, 5, DateTimeKind.Utc);
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        chain.Clock.UtcNow = start.AddSeconds(77_790);
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        Assert.Equal(1, chain.First.Calls);
        chain.Clock.UtcNow = start.AddSeconds(77_801);
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        Assert.Equal(2, chain.First.Calls);

        // A per-minute limit with its own wait: 40 s, whatever the growing pause would have been.
        chain.First.Answer = () => Chain.Failed(LlmOutcome.RateLimitedMinute) with { RetryAfter = TimeSpan.FromSeconds(40) };
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        Assert.Equal(3, chain.First.Calls);
        chain.First.Answer = Chain.Ok;
        chain.Clock.Advance(TimeSpan.FromSeconds(39));
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        Assert.Equal(3, chain.First.Calls);
        chain.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null));
        Assert.Equal(4, chain.First.Calls);
    }

    [PostgresFact]
    public async Task ACallCancelledByTheClient_IsStoredByPostgres_AndCountsInTheGroupBudget()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var chain = new Chain(database);
        chain.Options.GroupDailyCalls = 1;
        var group = Guid.NewGuid();
        using var client = new CancellationTokenSource();
        chain.First.Answer = () =>
        {
            client.Cancel();
            throw new OperationCanceledException(client.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chain.AskAsync(group, ct: client.Token));

        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage WHERE outcome = 'Cancelled' AND couple_id = @g AND input_tokens > 0", ("g", group)));
        chain.First.Answer = Chain.Ok;
        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, await chain.AskAsync(group));
        Assert.Equal(1, chain.First.Calls);
    }

    [PostgresFact]
    public async Task AKnownDailyLimit_IsCountedPerUtcDayAndModel_AndAnUnknownOneBlocksNothing()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var chain = new Chain(database);
        chain.Options.JobDailyCalls = 10_000;
        await database.ExecuteAsync(
            """
            INSERT INTO ai_usage (id, created_at_utc, day_utc, day_brt, provider, model, couple_id, feature, input_tokens, output_tokens, outcome, latency_ms)
            SELECT gen_random_uuid(), TIMESTAMPTZ '2026-10-08 12:00:00+00', DATE '2026-10-08', DATE '2026-10-08', 'gemini', 'gemini-flash-latest', NULL, 'insight_weekly', 900, 100, 'Ok', 20
            FROM generate_series(1, 300)
            """);

        // Unknown limit (the default of the Gemini models): 300 calls today block nothing.
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null, LlmFeatures.InsightWeekly, LlmCallMode.Job));
        Assert.Equal(1, chain.First.Calls);

        // Rpd = 334 → 300 usable (90%): 301 rows are there, the model is skipped and the reserve answers.
        chain.Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Flash, Rpd = 334 });
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null, LlmFeatures.InsightWeekly, LlmCallMode.Job));
        Assert.Equal((1, 1), (chain.First.Calls, chain.Second.Calls));

        // The provider's day is UTC: at 00:00 UTC the count starts again.
        chain.Clock.UtcNow = new DateTime(2026, 10, 9, 0, 0, 1, DateTimeKind.Utc);
        Assert.Equal(LlmGatewayOutcome.Ok, await chain.AskAsync(null, LlmFeatures.InsightWeekly, LlmCallMode.Job));
        Assert.Equal((2, 1), (chain.First.Calls, chain.Second.Calls));
    }

    private static async Task<List<string>> ColumnsOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
        => (await database.RowsAsync(
                "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, column_name"))
            .Where(r => tables.Contains((string)r[0]!))
            .Select(r => $"{r[0]}.{r[1]} {r[2]} {r[3]}")
            .ToList();

    // ---------------------------------------------------------------- the chain on this database

    /// <summary>Two Gemini links (stubs) and a gateway that is built again, on a new context, for every call.</summary>
    private sealed class Chain
    {
        private readonly TestDatabase _database;

        public Chain(TestDatabase database)
        {
            _database = database;
            Options.Chains[AiChains.Assistant] = [new LlmLink(Gemini, Flash), new LlmLink(Gemini, Lite)];
            Options.Chains[AiChains.Weekly] = [new LlmLink(Gemini, Flash), new LlmLink(Gemini, Lite)];
            // The pace per minute is not what these tests are about.
            Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Flash });
            Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Lite });
        }

        public AiOptions Options { get; } = new();

        public TestClock Clock { get; } = new() { UtcNow = new DateTime(2026, 10, 8, 13, 0, 0, DateTimeKind.Utc) };

        public Stub First { get; } = new(Flash);

        public Stub Second { get; } = new(Lite);

        public static LlmResult Ok() => new(LlmOutcome.Ok, """{"answer":"ok","refs":[]}""", 100, 50, null, 7);

        public static LlmResult Failed(LlmOutcome outcome) => new(outcome, null, 0, 0, "HTTP_429", 7);

        public async Task<LlmGatewayOutcome> AskAsync(
            Guid? coupleId, string feature = LlmFeatures.Chat, LlmCallMode mode = LlmCallMode.Interactive, CancellationToken ct = default)
        {
            await using var db = MigrationTests.Context(_database);
            var gateway = new LlmGateway(
                new Catalog(First, Second),
                new AiUsageRepository(db),
                new DeviceAiConsentGate(),
                Microsoft.Extensions.Options.Options.Create(Options),
                new LlmMinuteWindow(),
                Clock,
                new SystemLlmWaiter(),
                NullLogger<LlmGateway>.Instance);

            var request = new LlmRequest(
                feature,
                "Regras.",
                [new LlmMessage("user", "Pergunta de teste")],
                LlmJsonSchema.Object("answer", ("answer", LlmJsonSchema.String()), ("refs", LlmJsonSchema.Array(LlmJsonSchema.String()))),
                LlmFeatures.TemperatureOf(feature),
                1000);
            return (await gateway.GenerateAsync<Reply>(coupleId, request, mode, ct)).Outcome;
        }
    }

    private sealed record Reply(string Answer, IReadOnlyList<string> Refs);

    private sealed class TestClock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class Stub : ILlmProvider
    {
        public Stub(string model) => Model = model;

        public string Provider => Gemini;

        public string Model { get; }

        public LlmCapabilities Capabilities { get; } = new(true, false, false);

        public int Calls { get; private set; }

        public Func<LlmResult> Answer { get; set; } = Chain.Ok;

        public Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Answer());
        }
    }

    private sealed class Catalog : ILlmProviderCatalog
    {
        private readonly Stub[] _stubs;

        public Catalog(params Stub[] stubs) => _stubs = stubs;

        public bool AnyAvailable => true;

        public ILlmProvider? Find(string provider, string model) => _stubs.FirstOrDefault(s => s.Provider == provider && s.Model == model);
    }
}
