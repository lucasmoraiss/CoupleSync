using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Ai;
using CoupleSync.Domain.Entities;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #38 on real PostgreSQL: the migration that adds ai_consents and ai_user_preferences (additive, over existing
/// data), their columns and unique indexes, and what depends on the database — one row per (group, person, version)
/// even when two requests arrive together, the group filter, who leaves the group, and the consumption summed by
/// PostgreSQL per Brasília day. The AI provider is the fake one (no key, no call to anyone).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AiActivationTests
{
    private const string MigrationBefore = "20261008023328_AddAiUsage";
    private const string Status = "/api/v1/ai/status";
    private const string Consent = "/api/v1/ai/consent";

    private readonly PostgresServer _server;

    public AiActivationTests(PostgresServer server) => _server = server;

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_OnlyAddsTheTwoTables_AndKeepsEveryExistingRowAndColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBefore);

        var existingTables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        Assert.DoesNotContain("ai_consents", existingTables);
        Assert.DoesNotContain("ai_user_preferences", existingTables);
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

        var tablesAfter = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .Except(existingTables)
            .Order()
            .ToList();
        Assert.Equal(["ai_consents", "ai_user_preferences"], tablesAfter);
        // Nobody is switched on by the migration: every group starts "off" and is asked.
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM ai_user_preferences"));

        await using var db = MigrationTests.Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheMigration_CreatesTheColumnsAndTheUniqueIndexesOfTheDesign()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = "uuid not null",
                ["couple_id"] = "uuid not null",
                ["user_id"] = "uuid not null",
                ["version"] = "integer not null",
                ["accepted_at_utc"] = "timestamp with time zone not null",
                ["revoked_at_utc"] = "timestamp with time zone null",
                ["revoked_by_user_id"] = "uuid null",
            }.OrderBy(c => c.Key),
            (await TypedColumnsOfAsync(database, "ai_consents")).OrderBy(c => c.Key));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = "uuid not null",
                ["couple_id"] = "uuid not null",
                ["user_id"] = "uuid not null",
                ["weekly_email_enabled"] = "boolean not null",
                ["onboarding_answered_at_utc"] = "timestamp with time zone null",
                ["updated_at_utc"] = "timestamp with time zone not null",
            }.OrderBy(c => c.Key),
            (await TypedColumnsOfAsync(database, "ai_user_preferences")).OrderBy(c => c.Key));

        var consentIndexes = await IndexesOfAsync(database, "ai_consents");
        Assert.Contains(consentIndexes, i => i.Contains("UNIQUE") && i.Contains("(couple_id, user_id, version)"));
        var preferenceIndexes = await IndexesOfAsync(database, "ai_user_preferences");
        Assert.Contains(preferenceIndexes, i => i.Contains("UNIQUE") && i.Contains("(couple_id, user_id)"));
    }

    // ---------------------------------------------------------------- one row per (group, person, version)

    [PostgresFact]
    public async Task TheUniqueIndex_RefusesASecondRowOfTheSamePersonGroupAndVersion_AndAcceptingAgainUpdatesTheSameRow()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);
        var id = await database.ScalarAsync<Guid>("SELECT id FROM ai_consents");
        var firstDate = await database.ScalarAsync<DateTime>("SELECT accepted_at_utc FROM ai_consents");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            "INSERT INTO ai_consents (id, couple_id, user_id, version, accepted_at_utc) VALUES (@id, @couple, @user, 1, now())",
            ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("user", ana.UserId)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);
        Assert.Equal(ana.UserId, await database.ScalarAsync<Guid>("SELECT revoked_by_user_id FROM ai_consents"));
        Assert.False((await ana.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());

        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);

        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(id, await database.ScalarAsync<Guid>("SELECT id FROM ai_consents"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL AND revoked_by_user_id IS NULL"));
        Assert.True(await database.ScalarAsync<DateTime>("SELECT accepted_at_utc FROM ai_consents") > firstDate);
        Assert.True((await ana.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
    }

    [PostgresFact]
    public async Task TwoAcceptancesOfTheSamePersonAtTheSameTime_BothAnswer200_AndLeaveOneRow()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ana.Client.PostAsJsonAsync(Consent, new { Version = 1 })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_user_preferences"));
        var status = await ana.Client.GetFromJsonAsync<JsonElement>(Status);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("onboardingPending").GetBoolean());
    }

    // ---------------------------------------------------------------- groups and who leaves

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhoLeavesOrIsRemoved_HasTheAcceptanceRevokedAndThePreferencesDeleted_OnlyInThatGroup(bool removedByTheOwner)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        var group = ana.CoupleId!.Value;

        // Bruno switched it on; Ana only answered "not now"; another group (Carla) switched its own on.
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PatchAsJsonAsync("/api/v1/ai/preferences", new { OnboardingAnswered = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);
        var before = await ana.Client.GetFromJsonAsync<JsonElement>(Status);
        Assert.True(before.GetProperty("enabled").GetBoolean());
        Assert.Equal("Bruno", Assert.Single(before.GetProperty("acceptedBy").EnumerateArray()).GetProperty("name").GetString());

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());

        // His acceptance is still there, revoked; his preferences in the group are gone; Ana's stay.
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM ai_consents WHERE couple_id = @couple AND user_id = @user AND revoked_at_utc IS NOT NULL AND revoked_by_user_id = @user",
            ("couple", group), ("user", bruno.UserId)));
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM ai_user_preferences WHERE couple_id = @couple AND user_id = @user", ("couple", group), ("user", bruno.UserId)));
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM ai_user_preferences WHERE couple_id = @couple AND user_id = @user", ("couple", group), ("user", ana.UserId)));

        // It was the only acceptance: the group is off, and its chat is refused.
        var after = await ana.Client.GetFromJsonAsync<JsonElement>(Status);
        Assert.False(after.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Oi" })).StatusCode);

        // The other group was never touched.
        Assert.True((await carla.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM ai_consents WHERE couple_id = @couple AND revoked_at_utc IS NULL", ("couple", carla.CoupleId!.Value)));
    }

    // ---------------------------------------------------------------- the consumption, by PostgreSQL

    [PostgresFact]
    public async Task TheUsage_IsSummedByPostgres_PerBrasiliaDay_OnlyForTheGroupOfTheToken()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);

        // One real call each through the chain (the fake provider), then rows of other days written straight in.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?" })).StatusCode);
        var now = DateTime.UtcNow;
        await using (var db = factory.NewContext())
        {
            for (var i = 0; i < 3; i++)
                db.AiUsages.Add(AiUsage.Record(now.AddDays(-2), "fake", "fake", ana.CoupleId, LlmFeatures.Chat, 100, 20, "Ok", 5));
            db.AiUsages.Add(AiUsage.Record(now.AddDays(-2), "fake", "fake", ana.CoupleId, LlmFeatures.Chat, 0, 0, "Error", 5));
            // Outside the window asked below, and of the other group: neither may show up.
            db.AiUsages.Add(AiUsage.Record(now.AddDays(-20), "fake", "fake", ana.CoupleId, LlmFeatures.Chat, 100, 20, "Ok", 5));
            for (var i = 0; i < 7; i++)
                db.AiUsages.Add(AiUsage.Record(now.AddDays(-2), "fake", "fake", carla.CoupleId, LlmFeatures.Chat, 100, 20, "Ok", 5));
            await db.SaveChangesAsync();
        }

        var usage = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/ai/usage?days=7");

        var days = usage.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(7, days.Count);
        Assert.Equal(5, days.Sum(d => d.GetProperty("calls").GetInt32()));
        Assert.Equal(1, days.Sum(d => d.GetProperty("failures").GetInt32()));
        Assert.Equal(1, days[^1].GetProperty("calls").GetInt32());
        var twoDaysAgo = days[^3];
        Assert.Equal((4, 1, 300L, 60L), (
            twoDaysAgo.GetProperty("calls").GetInt32(),
            twoDaysAgo.GetProperty("failures").GetInt32(),
            twoDaysAgo.GetProperty("inputTokens").GetInt64(),
            twoDaysAgo.GetProperty("outputTokens").GetInt64()));
        Assert.Equal(5, Assert.Single(usage.GetProperty("byFeature").EnumerateArray()).GetProperty("calls").GetInt32());
        Assert.Equal(1, usage.GetProperty("groupBudget").GetProperty("callsToday").GetInt32());

        var model = Assert.Single(usage.GetProperty("providersToday").EnumerateArray());
        Assert.Equal(("fake", 1), (model.GetProperty("model").GetString(), model.GetProperty("calls").GetInt32()));
        Assert.Equal(JsonValueKind.Null, model.GetProperty("limit").ValueKind);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("percentUsed").ValueKind);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The same API with the fake AI provider on: available, and no provider key anywhere.</summary>
    private static DerivedTestHost WithTheFakeProvider(PostgresApiFactory factory)
        => factory.WithTestHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:UseFakeProvider"] = "true",
                // Empty (not absent) so that a value in the machine's environment can never leak into the test.
                ["GEMINI_API_KEY"] = string.Empty,
                ["Gemini:ApiKey"] = string.Empty,
                ["RENDER"] = string.Empty,
            })));

    private static async Task<Dictionary<string, string>> TypedColumnsOfAsync(TestDatabase database, string table)
        => (await database.RowsAsync(
                $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToDictionary(r => (string)r[0]!, r => $"{r[1]} {((string)r[2]! == "YES" ? "null" : "not null")}");

    private static async Task<List<string>> IndexesOfAsync(TestDatabase database, string table)
        => (await database.RowsAsync($"SELECT indexdef FROM pg_indexes WHERE tablename = '{table}' ORDER BY indexname"))
            .Select(r => (string)r[0]!)
            .ToList();

    private static async Task<List<string>> ColumnsOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
    {
        var rows = await database.RowsAsync(
            "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, column_name");
        return rows
            .Where(r => tables.Contains((string)r[0]!))
            .Select(r => $"{r[0]}.{r[1]} {r[2]} {r[3]}")
            .ToList();
    }
}
