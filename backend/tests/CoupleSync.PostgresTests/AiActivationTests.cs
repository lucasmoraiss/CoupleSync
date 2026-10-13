using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Ai;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CoupleSync.PostgresTests;

/// <summary>
/// The AI of a group on real PostgreSQL: the migration that adds ai_consents and ai_user_preferences (additive, over
/// existing data), their columns and unique indexes, and what depends on the database — one row per (group, person,
/// version) even when two requests arrive together, "off for the group" stored in the group's own column (the AI is
/// on for every group until then, whatever ai_consents holds), the group filter, who leaves the group, and the
/// consumption summed by PostgreSQL per Brasília day. The AI provider is the fake one (no key, no call to anyone).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AiActivationTests
{
    // The migration right before AddAiConsents (regenerated after the Open Finance one came in from main).
    private const string MigrationBefore = "20261008150513_AddOpenFinanceSync";

    // This migration itself: what "only adds the two tables" is said of (later migrations change other tables).
    private const string MigrationUnderTest = "20261008185726_AddAiConsents";

    // The migration that adds "off for the group" (couples.ai_disabled_at_utc), and the one right before it.
    private const string AiOffMigrationBefore = "20261009022052_AddCreditCardBills";
    private const string AiOffMigration = "20261013141858_AddCoupleAiDisabledAt";
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

        await MigrationTests.MigrateAsync(database, MigrationUnderTest);

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
        // The migration stores no acceptance and no answer for anyone.
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM ai_user_preferences"));

        await MigrationTests.MigrateAsync(database);
        await using var db = MigrationTests.Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// Over a database as production has it — groups, money, and the AI rows of before (an acceptance of version 1,
    /// a revoked one of version 2, a "not now") — the migration only adds one nullable column to couples, touches no
    /// row, and leaves every group that existed with the AI on.
    /// </summary>
    [PostgresFact]
    public async Task TheAiOffMigration_OnlyAddsOneNullableColumnToCouples_KeepsEveryRow_AndEveryGroupThatExistedIsOn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, AiOffMigrationBefore);

        var member = (await database.RowsAsync("SELECT couple_id, user_id FROM couple_members ORDER BY couple_id, user_id LIMIT 1")).Single();
        var (group, person) = ((Guid)member[0]!, (Guid)member[1]!);
        await database.ExecuteAsync(
            "INSERT INTO ai_consents (id, couple_id, user_id, version, accepted_at_utc) VALUES (@id, @couple, @user, 1, now() - interval '9 days')",
            ("id", Guid.NewGuid()), ("couple", group), ("user", person));
        await database.ExecuteAsync(
            "INSERT INTO ai_consents (id, couple_id, user_id, version, accepted_at_utc, revoked_at_utc, revoked_by_user_id) "
            + "VALUES (@id, @couple, @user, 2, now() - interval '3 days', now() - interval '2 days', @user)",
            ("id", Guid.NewGuid()), ("couple", group), ("user", person));
        await database.ExecuteAsync(
            "INSERT INTO ai_user_preferences (id, couple_id, user_id, weekly_email_enabled, onboarding_answered_at_utc, updated_at_utc) "
            + "VALUES (@id, @couple, @user, false, now() - interval '2 days', now() - interval '2 days')",
            ("id", Guid.NewGuid()), ("couple", group), ("user", person));

        var tables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        var countsBefore = new Dictionary<string, long>();
        foreach (var table in tables) countsBefore[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(countsBefore["transactions"] > 0 && countsBefore["couples"] > 0, "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var columnsBefore = await ColumnsOfAsync(database, tables);
        const string aiRows = "SELECT id::text || '|' || version || '|' || accepted_at_utc::text || '|' || coalesce(revoked_at_utc::text, '-') "
                              + "|| '|' || coalesce(revoked_by_user_id::text, '-') FROM ai_consents ORDER BY id";
        var consentsBefore = (await database.RowsAsync(aiRows)).Select(r => (string)r[0]!).ToList();
        Assert.Equal(2, consentsBefore.Count);

        await MigrationTests.MigrateAsync(database, AiOffMigration);

        var tablesAfter = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        Assert.Equal(tables, tablesAfter);
        foreach (var table in tables)
            Assert.Equal(countsBefore[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(consentsBefore, (await database.RowsAsync(aiRows)).Select(r => (string)r[0]!).ToList());

        var columnsAfter = await ColumnsOfAsync(database, tables);
        Assert.Equal(["couples.ai_disabled_at_utc timestamp with time zone YES"], columnsAfter.Except(columnsBefore).ToList());
        Assert.Empty(columnsBefore.Except(columnsAfter));

        // No group that existed is "off": the column is empty in every row, and the code that decides reads "on".
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM couples WHERE ai_disabled_at_utc IS NOT NULL"));
        await MigrationTests.MigrateAsync(database);
        await using var db = MigrationTests.Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var gate = new ServerAiConsentGate(new AiActivationRepository(db));
        var groups = (await database.RowsAsync("SELECT id FROM couples")).Select(r => (Guid)r[0]!).ToList();
        Assert.Contains(group, groups);
        foreach (var id in groups) Assert.True(await gate.IsEnabledAsync(id, CancellationToken.None), $"the group {id} should be on");
        Assert.False(await gate.IsEnabledAsync(null, CancellationToken.None));
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

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        var id = await database.ScalarAsync<Guid>("SELECT id FROM ai_consents");
        var firstDate = await database.ScalarAsync<DateTime>("SELECT accepted_at_utc FROM ai_consents");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            "INSERT INTO ai_consents (id, couple_id, user_id, version, accepted_at_utc) VALUES (@id, @couple, @user, 2, now())",
            ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("user", ana.UserId)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);
        Assert.Equal(ana.UserId, await database.ScalarAsync<Guid>("SELECT revoked_by_user_id FROM ai_consents"));
        // Taking the own acceptance back does not switch the group off.
        Assert.True((await ana.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());

        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);

        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(id, await database.ScalarAsync<Guid>("SELECT id FROM ai_consents"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL AND revoked_by_user_id IS NULL"));
        Assert.True(await database.ScalarAsync<DateTime>("SELECT accepted_at_utc FROM ai_consents") > firstDate);
        Assert.True((await ana.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// The states production has today — only a row of version 1, rows revoked by the old "off for the group", the
    /// "not now" stored — are not "off": the group is on, nobody is asked, the chat answers, and no version is refused.
    /// </summary>
    [PostgresFact]
    public async Task AGroupWithOnlyOldRows_Version1OrRevoked_IsOn_NobodyIsAsked_AndTheChatAnswers()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PatchAsJsonAsync("/api/v1/ai/preferences", new { OnboardingAnswered = true })).StatusCode);
        await database.ExecuteAsync(
            "INSERT INTO ai_consents (id, couple_id, user_id, version, accepted_at_utc) VALUES (@id, @couple, @user, 1, now() - interval '2 hours')",
            ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("user", ana.UserId));
        await database.ExecuteAsync(
            "INSERT INTO ai_consents (id, couple_id, user_id, version, accepted_at_utc, revoked_at_utc, revoked_by_user_id) "
            + "VALUES (@id, @couple, @user, 2, now() - interval '2 hours', now() - interval '1 hour', @by)",
            ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("user", bruno.UserId), ("by", ana.UserId));

        foreach (var member in new[] { ana, bruno })
        {
            var status = await member.Client.GetFromJsonAsync<JsonElement>(Status);
            Assert.Equal(2, status.GetProperty("consentVersion").GetInt32());
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            Assert.Equal(0, status.GetProperty("acceptedBy").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("myAcceptance").ValueKind);
            Assert.Equal(HttpStatusCode.OK, (await member.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?" })).StatusCode);
        }

        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage"));

        // No version is refused; only the one in force is written as an acceptance.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents WHERE version = 2 AND revoked_at_utc IS NULL"));
        // The row of version 1 and the revoked one are as they were.
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NULL"));
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM ai_consents WHERE version = 2 AND user_id = @user AND revoked_at_utc IS NOT NULL", ("user", bruno.UserId)));
    }

    /// <summary>
    /// "Off for the group" is a value of the group itself (couples.ai_disabled_at_utc): it is stored for the group of
    /// the token only, stops the chat of every member, and activating clears it. "Mine" never writes it.
    /// </summary>
    [PostgresFact]
    public async Task OffForTheGroup_IsStoredInTheGroupsOwnColumn_OnlyForTheGroupOfTheToken_AndActivatingClearsIt()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        var group = ana.CoupleId!.Value;

        Task<long> OffGroupsAsync() => database.ScalarAsync<long>("SELECT count(*) FROM couples WHERE ai_disabled_at_utc IS NOT NULL");
        Assert.Equal(0, await OffGroupsAsync());

        // "Mine" by who has no acceptance: 200 and nothing is switched off.
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);
        Assert.Equal(0, await OffGroupsAsync());

        var before = DateTime.UtcNow.AddSeconds(-5);
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.DeleteAsync($"{Consent}?scope=group")).StatusCode);

        Assert.Equal(1, await OffGroupsAsync());
        var offAt = await database.ScalarAsync<DateTime>("SELECT ai_disabled_at_utc FROM couples WHERE id = @id", ("id", group));
        Assert.InRange(offAt.ToUniversalTime(), before, DateTime.UtcNow.AddSeconds(5));
        foreach (var member in new[] { ana, bruno })
        {
            var status = await member.Client.GetFromJsonAsync<JsonElement>(Status);
            Assert.False(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            var chat = await member.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?" });
            Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
            Assert.Equal("AI_CONSENT_REQUIRED", (await chat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM ai_usage"));

        // The other group was never touched.
        Assert.True((await carla.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?" })).StatusCode);

        // "Ativar", by the other member: the column is cleared and both are answered again.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.Equal(0, await OffGroupsAsync());
        Assert.True((await bruno.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?" })).StatusCode);
    }

    [PostgresFact]
    public async Task SwitchingOffAndOnAtTheSameTime_ByTwoMembers_AllAnswer200_AndTheStatusAgreesWithTheColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? bruno.Client.DeleteAsync($"{Consent}?scope=group")
            : ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var off = await database.ScalarAsync<long>(
            "SELECT count(*) FROM couples WHERE id = @id AND ai_disabled_at_utc IS NOT NULL", ("id", ana.CoupleId!.Value)) == 1;
        Assert.Equal(!off, (await ana.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
        Assert.Equal(!off, (await bruno.Client.GetFromJsonAsync<JsonElement>(Status)).GetProperty("enabled").GetBoolean());
        Assert.True(await database.ScalarAsync<long>("SELECT count(*) FROM ai_consents") <= 1);
    }

    [PostgresFact]
    public async Task TwoAcceptancesOfTheSamePersonAtTheSameTime_BothAnswer200_AndLeaveOneRow()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheFakeProvider(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })));

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
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PatchAsJsonAsync("/api/v1/ai/preferences", new { OnboardingAnswered = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
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

        // It was the only acceptance, and it was not what kept the AI on: the group is still on and is answered.
        var after = await ana.Client.GetFromJsonAsync<JsonElement>(Status);
        Assert.True(after.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, after.GetProperty("acceptedBy").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Oi" })).StatusCode);

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
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);

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
