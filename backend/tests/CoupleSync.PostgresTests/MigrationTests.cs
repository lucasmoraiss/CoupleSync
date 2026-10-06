using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoupleSync.PostgresTests;

/// <summary>Every migration on real PostgreSQL: from zero, and over data shaped like production before this series.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationTests
{
    private readonly PostgresServer _server;

    public MigrationTests(PostgresServer server) => _server = server;

    internal static AppDbContext Context(TestDatabase database) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options);

    internal static async Task MigrateAsync(TestDatabase database, string? target = null)
    {
        await using var db = Context(database);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    [PostgresFact]
    public async Task AllMigrations_ApplyFromZero_AndLeaveNothingPending()
    {
        await using var database = await _server.CreateDatabaseAsync();

        await MigrateAsync(database);

        await using var db = Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Database.CanConnectAsync());
        Assert.Equal(
            db.Database.GetMigrations().Count(),
            (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [PostgresFact]
    public async Task AllMigrations_CanBeRolledBackToZeroAndReapplied()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrateAsync(database);

        await MigrateAsync(database, Migration.InitialDatabase);
        await MigrateAsync(database);

        await using var db = Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheLegacyDataMigrates_EveryDataMigrationDoesItsJob_AndNothingIsLost()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        var seed = new LegacyDataSeed();
        await seed.SeedAsync(database, includeOrphans: false);

        var tables = new[]
        {
            "users", "couples", "transactions", "transaction_event_ingests", "budget_plans", "goals", "income_sources",
            "category_rules", "device_tokens", "notification_settings", "notification_events", "import_jobs", "refresh_tokens",
        };
        var before = new Dictionary<string, long>();
        foreach (var table in tables) before[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM {table}");
        var transactionSumBefore = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var allocationSumBefore = await database.ScalarAsync<decimal>("SELECT sum(allocated_amount) FROM budget_allocations");
        var beforeMigration = DateTime.UtcNow;

        await MigrateAsync(database);

        var afterMigration = DateTime.UtcNow;

        // Nothing was lost: same row count everywhere except the two places that merge/dedupe on purpose.
        foreach (var table in tables)
        {
            var expected = table == "device_tokens" ? before[table] - 2 : before[table];
            Assert.Equal(expected, await database.ScalarAsync<long>($"SELECT count(*) FROM {table}"));
        }

        Assert.Equal(transactionSumBefore, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(allocationSumBefore, await database.ScalarAsync<decimal>("SELECT sum(allocated_amount) FROM budget_allocations"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM email_codes"));

        // --- NormalizeCategoriesAndCurrency: transactions
        var transactions = (await database.RowsAsync("SELECT fingerprint, category, currency FROM transactions"))
            .ToDictionary(r => (string)r[0]!, r => ((string)r[1]!, (string)r[2]!));
        Assert.Equal(("ALIMENTACAO", "BRL"), transactions["fp-1"]);
        Assert.Equal(("ALIMENTACAO", "BRL"), transactions["fp-2"]);
        Assert.Equal(("ALIMENTACAO", "BRL"), transactions["fp-3"]);
        Assert.Equal(("SAUDE", "BRL"), transactions["fp-4"]);
        Assert.Equal(("SAUDE", "BRL"), transactions["fp-5"]);
        Assert.Equal(("TRANSPORTE", "BRL"), transactions["fp-6"]);
        Assert.Equal(("OUTROS", "BRL"), transactions["fp-7"]);   // free text
        Assert.Equal(("OUTROS", "USD"), transactions["fp-8"]);   // other currencies are not touched
        Assert.Equal(("LAZER", "EUR"), transactions["fp-9"]);
        Assert.Equal(("OUTROS", "BRL"), transactions["fp-10"]);
        Assert.Equal(("MORADIA", "BRL"), transactions["fp-11"]);
        Assert.Equal(("MORADIA", "BRL"), transactions["fp-12"]); // accent in a different place
        Assert.Equal(("OUTROS", "BRL"), transactions["fp-13"]);

        // --- category rules
        var rules = (await database.RowsAsync("SELECT keyword, category FROM category_rules"))
            .ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        Assert.Equal("ALIMENTACAO", rules["ifood"]);
        Assert.Equal("TRANSPORTE", rules["uber"]);
        Assert.Equal("SAUDE", rules["drogaria"]);
        Assert.Equal("OUTROS", rules["misterio"]);
        Assert.Equal("LAZER", rules["cinema"]);

        // --- budget allocations: colliding rows of one plan are summed into the oldest one
        var plan1 = (await database.RowsAsync(
                $"SELECT category, currency, allocated_amount, created_at_utc FROM budget_allocations WHERE budget_plan_id = '{seed.Plan1}'"))
            .ToDictionary(r => $"{r[0]}/{r[1]}", r => ((decimal)r[2]!, (DateTime)r[3]!));
        Assert.Equal(5, plan1.Count);
        Assert.Equal(175m, plan1["ALIMENTACAO/BRL"].Item1);
        Assert.Equal(new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc), plan1["ALIMENTACAO/BRL"].Item2.ToUniversalTime());
        Assert.Equal(200m, plan1["SAUDE/BRL"].Item1);
        Assert.Equal(15m, plan1["LAZER/USD"].Item1);
        Assert.Equal(20m, plan1["LAZER/BRL"].Item1);
        Assert.Equal(70m, plan1["OUTROS/BRL"].Item1);
        var plan2 = await database.RowsAsync($"SELECT category, currency, allocated_amount FROM budget_allocations WHERE budget_plan_id = '{seed.Plan2}'");
        var single = Assert.Single(plan2);
        Assert.Equal(("ALIMENTACAO", "BRL", 300m), ((string)single[0]!, (string)single[1]!, (decimal)single[2]!));

        // --- currency spelling on every table (other currencies stay)
        foreach (var table in new[] { "budget_plans", "goals" })
        {
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE currency <> 'BRL'"));
        }

        Assert.Equal(new[] { "BRL", "EUR" }, (await database.RowsAsync("SELECT DISTINCT currency FROM income_sources ORDER BY 1")).Select(r => (string)r[0]!).ToArray());

        // --- DeviceTokenUniquePerToken: the newest row of the shared token survives, under U2
        var tokens = (await database.RowsAsync("SELECT token, user_id FROM device_tokens"))
            .ToDictionary(r => (string)r[0]!, r => (Guid)r[1]!);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(seed.U2, tokens["tok-shared"]);
        Assert.Equal(seed.U4, tokens["tok-own"]);
        Assert.True(await database.ScalarAsync<bool>(
            "SELECT indisunique FROM pg_index WHERE indexrelid = 'public.\"IX_device_tokens_token\"'::regclass"));

        // --- AddCoupleOwnerAndJoinCodeExpiry: oldest member owns the group, codes live 7 days from the migration
        var couples = (await database.RowsAsync("SELECT id, owner_user_id, join_code, join_code_expires_at_utc FROM couples"))
            .ToDictionary(r => (Guid)r[0]!, r => (Owner: (Guid?)r[1], Code: (string)r[2]!, Expires: ((DateTime)r[3]!).ToUniversalTime()));
        Assert.Equal(seed.U1, couples[seed.Couple1].Owner);   // joined first, although U2 has the oldest account
        Assert.Equal(seed.U5, couples[seed.Couple2].Owner);   // no join date: the oldest account
        Assert.Null(couples[seed.Couple3].Owner);             // nobody in the group
        Assert.Equal("ABC123", couples[seed.Couple1].Code);
        foreach (var (_, value) in couples)
        {
            Assert.InRange(value.Expires, beforeMigration.AddDays(7).AddMinutes(-1), afterMigration.AddDays(7).AddMinutes(1));
        }

        Assert.Equal(8, await database.ScalarAsync<int>(
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_name = 'couples' AND column_name = 'join_code'"));

        // --- AddEmailCodesAndEmailVerified
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM users WHERE email_verified"));

        // --- import jobs: every status survives, with the new columns at their neutral value
        var jobs = (await database.RowsAsync(
                "SELECT id, status, line_states_json, source_file_name, ai_categorization_consent, ocr_result_json::text, retry_count FROM import_jobs"))
            .ToDictionary(r => (Guid)r[0]!);
        Assert.Equal(
            new[] { "Confirmed", "Failed", "Pending", "Processing", "Ready" }.OrderBy(x => x),
            jobs.Values.Select(r => (string)r[1]!).OrderBy(x => x));
        Assert.All(jobs.Values, r =>
        {
            Assert.Null(r[2]);
            Assert.Null(r[3]);
            Assert.False((bool)r[4]!);
        });
        Assert.Contains("Mercado", (string)jobs[seed.JobReady][5]!);
        Assert.Equal(3, (int)jobs[seed.JobFailed][6]!);

        // --- every foreign key is in place and validated (no orphan existed)
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND NOT convalidated"));
        Assert.True(await database.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND conname LIKE 'FK_%'") >= 22);

        // --- the migrated data is readable through the real application
        await AssertLegacyDataIsReadableThroughTheApiAsync(database, seed);
    }

    private async Task AssertLegacyDataIsReadableThroughTheApiAsync(TestDatabase database, LegacyDataSeed seed)
    {
        await using var factory = new PostgresApiFactory(database);
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = "u1@legacy.test", Password = LegacyDataSeed.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{seed.JobReady}/status");
        Assert.Equal("Ready", status.GetProperty("status").GetString());
        var results = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{seed.JobReady}/results");
        Assert.Equal(2, results.GetProperty("candidates").GetArrayLength());
        var failed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ocr/{seed.JobFailed}/status");
        Assert.Equal("Failed", failed.GetProperty("status").GetString());

        var transactions = await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions?pageSize=50");
        Assert.Equal(10, transactions.GetProperty("totalCount").GetInt32());
        Assert.All(transactions.GetProperty("items").EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("category").GetString(), new[] { "ALIMENTACAO", "SAUDE", "TRANSPORTE", "OUTROS", "LAZER" }));
    }

    [PostgresFact]
    public async Task LegacyOrphanRows_DerivedDataIsCleaned_UserDataIsKept_AndTheKeysStayNotValid()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        var seed = new LegacyDataSeed();
        await seed.SeedAsync(database, includeOrphans: true);

        await MigrateAsync(database);

        // Derived data pointing to nothing is gone; the healthy rows next to it stay.
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens WHERE token LIKE 'tok-ghost%'"));
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM notification_settings WHERE user_id = '{seed.Ghost}'"));
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM notification_events WHERE couple_id = '{seed.Ghost}'"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM notification_settings"));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM notification_events"));

        // The users' own data is never deleted to satisfy a key.
        Assert.Equal(14, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(14, await database.ScalarAsync<long>("SELECT count(*) FROM transaction_event_ingests"));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM goals"));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM income_sources"));
        Assert.Equal(6, await database.ScalarAsync<long>("SELECT count(*) FROM import_jobs"));

        // The keys with orphans exist but stay NOT VALID; the ones without orphans were validated.
        var notValid = (await database.RowsAsync("SELECT conname FROM pg_constraint WHERE contype = 'f' AND NOT convalidated"))
            .Select(r => (string)r[0]!).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[]
            {
                "FK_goals_users_created_by_user_id",
                "FK_import_jobs_couples_couple_id",
                "FK_import_jobs_users_user_id",
                "FK_income_sources_users_user_id",
                "FK_transaction_event_ingests_couples_couple_id",
                "FK_transaction_event_ingests_users_user_id",
                "FK_transactions_couples_couple_id",
                "FK_transactions_users_user_id",
            },
            notValid);

        // A NOT VALID key still refuses new orphans.
        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => database.ExecuteAsync($"""
            INSERT INTO income_sources (id, amount, couple_id, created_at_utc, currency, is_recurring, is_shared, month, name, updated_at_utc, user_id)
            VALUES ('{Guid.NewGuid()}', 1, '{seed.Couple1}', now(), 'BRL', false, false, '2026-09', 'novo', now(), '{Guid.NewGuid()}')
            """));
        Assert.Equal("23503", ex.SqlState);
    }
}
