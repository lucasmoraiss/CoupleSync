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

    internal static AppDbContext Context(TestDatabase database)
    {
        database.Server.Guard(database.ConnectionString);
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options);
    }

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
    public async Task TheForeignKeyMigration_FailsFastOnALockHeldElsewhere_RollsBackWhole_AndAppliesOnceTheLockIsGone()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrateAsync(database, "20261006210924_AddImportJobAiCategorizationConsent");

        await using (var blocker = await database.OpenAsync())
        {
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var lockCommand = new Npgsql.NpgsqlCommand("LOCK TABLE transactions IN ROW EXCLUSIVE MODE", blocker, transaction))
            {
                await lockCommand.ExecuteNonQueryAsync();
            }

            var started = DateTime.UtcNow;
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(database));
            var postgres = failure as Npgsql.PostgresException ?? failure.InnerException as Npgsql.PostgresException;
            Assert.NotNull(postgres);
            Assert.Equal("55P03", postgres!.SqlState); // lock_not_available
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
            await transaction.RollbackAsync();
        }

        // Nothing of the migration stayed behind (it rolled back whole)...
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname LIKE 'FK_transactions_%users%'"));
        // ...and with the lock gone it applies.
        await MigrateAsync(database);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'FK_transactions_users_user_id'"));
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
        foreach (var (id, value) in couples.Where(c => c.Key != seed.Couple3))
        {
            Assert.InRange(value.Expires, beforeMigration.AddDays(7).AddMinutes(-1), afterMigration.AddDays(7).AddMinutes(1));
        }

        // --- AddCoupleMembers: the single group of each user becomes a membership row; users.couple_id stays (active group)
        var memberships = (await database.RowsAsync("SELECT couple_id, user_id, role, joined_at_utc FROM couple_members"))
            .ToDictionary(r => (Guid)r[1]!, r => (Couple: (Guid)r[0]!, Role: (string)r[2]!, Joined: ((DateTime)r[3]!).ToUniversalTime()));
        Assert.Equal(5, memberships.Count);
        Assert.Equal((seed.Couple1, "Owner", new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc)), memberships[seed.U1]);
        Assert.Equal((seed.Couple1, "Member", new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc)), memberships[seed.U2]);
        Assert.Equal((seed.Couple1, "Member", new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc)), memberships[seed.U3]);
        Assert.Equal((seed.Couple2, "Member", new DateTime(2026, 2, 2, 12, 0, 0, DateTimeKind.Utc)), memberships[seed.U4]); // no join date: account creation
        Assert.Equal((seed.Couple2, "Owner", new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc)), memberships[seed.U5]);
        Assert.DoesNotContain(seed.U6, memberships.Keys); // never had a group
        var activeGroups = (await database.RowsAsync("SELECT id, couple_id FROM users"))
            .ToDictionary(r => (Guid)r[0]!, r => (Guid?)r[1]);
        Assert.Equal(seed.Couple1, activeGroups[seed.U1]);
        Assert.Equal(seed.Couple1, activeGroups[seed.U2]);
        Assert.Equal(seed.Couple1, activeGroups[seed.U3]);
        Assert.Equal(seed.Couple2, activeGroups[seed.U4]);
        Assert.Equal(seed.Couple2, activeGroups[seed.U5]);
        Assert.Null(activeGroups[seed.U6]);
        // The group nobody is in can no longer be joined: its code expired when the migration ran.
        Assert.InRange(couples[seed.Couple3].Expires, beforeMigration.AddMinutes(-1), afterMigration.AddMinutes(1));
        // Alert preferences: one row per user AND group from now on (the old index allowed one group only).
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_notification_settings_user_id'"));
        Assert.True(await database.ScalarAsync<bool>(
            "SELECT indisunique FROM pg_index WHERE indexrelid = 'public.\"IX_notification_settings_user_id_couple_id\"'::regclass"));

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

        // The legacy user is in exactly their old group, as its owner, and it is the active one...
        var groups = await client.GetFromJsonAsync<JsonElement>("/api/v1/couples");
        Assert.Equal(seed.Couple1, groups.GetProperty("activeCoupleId").GetGuid());
        var only = Assert.Single(groups.GetProperty("groups").EnumerateArray().ToList());
        Assert.Equal(seed.Couple1, only.GetProperty("coupleId").GetGuid());
        Assert.True(only.GetProperty("isOwner").GetBoolean());
        Assert.Equal("Grupo com Carla e Bruno", only.GetProperty("name").GetString());
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me");
        Assert.Equal(3, me.GetProperty("members").GetArrayLength());
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/settings");
        Assert.False(settings.GetProperty("lowBalanceEnabled").GetBoolean()); // the preference saved before the migration

        // ...and can now open a second group, where nothing of the first shows, and come back.
        var created = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions?pageSize=50")).GetProperty("totalCount").GetInt32());
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/settings")).GetProperty("lowBalanceEnabled").GetBoolean());
        var back = await client.PostAsJsonAsync("/api/v1/couples/switch", new { coupleId = seed.Couple1 });
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        Assert.Equal(10, (await client.GetFromJsonAsync<JsonElement>("/api/v1/transactions?pageSize=50")).GetProperty("totalCount").GetInt32());
    }

    private const string MigrationBeforeMemberships = "20261006211931_AddForeignKeysForCoupleAndUserRelations";

    /// <summary>
    /// Data as the release before the membership table could leave it, including what its unserialised
    /// leave/join could produce: an owner who is not a member and an empty group whose code still works.
    /// </summary>
    [PostgresFact]
    public async Task TheMembershipMigration_BackfillsRoles_RepairsBrokenGroups_KeepsEverything_AndCanBeUndone()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrateAsync(database, MigrationBeforeMemberships);

        Guid healthy = Guid.NewGuid(), strayOwner = Guid.NewGuid(), noOwner = Guid.NewGuid(), abandoned = Guid.NewGuid(), longEmpty = Guid.NewGuid();
        Guid owner = Guid.NewGuid(), older = Guid.NewGuid(), inactive = Guid.NewGuid(), p = Guid.NewGuid(), q = Guid.NewGuid(),
            r = Guid.NewGuid(), loner = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT INTO couples (id, created_at, join_code, status, owner_user_id, join_code_expires_at_utc) VALUES
              ('{healthy}',    '2026-01-01T12:00:00Z', 'HEALTHY1', 'Active', '{owner}', now() + interval '5 days'),
              ('{strayOwner}', '2026-01-01T12:00:00Z', 'STRAY123', 'Active', '{owner}', now() + interval '5 days'),
              ('{noOwner}',    '2026-01-01T12:00:00Z', 'NOOWNER1', 'Active', NULL,      now() + interval '5 days'),
              ('{abandoned}',  '2026-01-01T12:00:00Z', 'ABANDON1', 'Active', '{loner}', now() + interval '5 days'),
              ('{longEmpty}',  '2026-01-01T12:00:00Z', 'LONGEMP1', 'Active', NULL,      '2026-02-01T12:00:00Z');

            INSERT INTO users (id, couple_id, couple_joined_at_utc, created_at_utc, email, is_active, name, password_hash, email_verified) VALUES
              ('{owner}',    '{healthy}',    '2026-03-05T12:00:00Z', '2026-01-01T12:00:00Z', 'owner@m.test',    true,  'Dona',    'x', false),
              ('{older}',    '{healthy}',    '2026-03-01T12:00:00Z', '2026-01-01T12:00:00Z', 'older@m.test',    true,  'Antigo',  'x', false),
              ('{inactive}', '{healthy}',    '2026-03-09T12:00:00Z', '2026-01-01T12:00:00Z', 'inactive@m.test', false, 'Inativa', 'x', false),
              ('{p}',        '{strayOwner}', '2026-02-02T12:00:00Z', '2026-01-01T12:00:00Z', 'p@m.test',        true,  'Paula',   'x', false),
              ('{q}',        '{strayOwner}', '2026-02-01T12:00:00Z', '2026-01-01T12:00:00Z', 'q@m.test',        true,  'Quico',   'x', false),
              ('{r}',        '{noOwner}',    NULL,                   '2026-01-07T12:00:00Z', 'r@m.test',        true,  'Rita',    'x', false),
              ('{loner}',    NULL,           NULL,                   '2026-01-01T12:00:00Z', 'loner@m.test',    true,  'Só',      'x', false);

            INSERT INTO notification_settings (id, bill_reminder_enabled, couple_id, large_transaction_enabled, low_balance_enabled, updated_at_utc, user_id) VALUES
              ('{Guid.NewGuid()}', true, '{healthy}', false, true, '2026-09-01T12:00:00Z', '{owner}');
            """);
        var usersBefore = await database.RowsAsync("SELECT id, couple_id, couple_joined_at_utc FROM users ORDER BY id");
        var beforeMigration = DateTime.UtcNow;

        await MigrateAsync(database);

        var afterMigration = DateTime.UtcNow;
        var rows = (await database.RowsAsync("SELECT user_id, couple_id, role, joined_at_utc FROM couple_members"))
            .ToDictionary(x => (Guid)x[0]!, x => (Couple: (Guid)x[1]!, Role: (string)x[2]!, Joined: ((DateTime)x[3]!).ToUniversalTime()));
        Assert.Equal(6, rows.Count);
        // The registered owner keeps the role even though another member joined earlier.
        Assert.Equal((healthy, "Owner", new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc)), rows[owner]);
        Assert.Equal((healthy, "Member", new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)), rows[older]);
        Assert.Equal((healthy, "Member", new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc)), rows[inactive]); // nothing is lost for a deactivated account
        // Registered owner was not a member: the oldest member takes over.
        Assert.Equal((strayOwner, "Owner"), (rows[q].Couple, rows[q].Role));
        Assert.Equal((strayOwner, "Member"), (rows[p].Couple, rows[p].Role));
        Assert.Equal((noOwner, "Owner", new DateTime(2026, 1, 7, 12, 0, 0, DateTimeKind.Utc)), rows[r]);
        Assert.DoesNotContain(loner, rows.Keys);

        var groups = (await database.RowsAsync("SELECT id, owner_user_id, join_code, join_code_expires_at_utc FROM couples"))
            .ToDictionary(x => (Guid)x[0]!, x => (Owner: (Guid?)x[1], Code: (string)x[2]!, Expires: ((DateTime)x[3]!).ToUniversalTime()));
        Assert.Equal(owner, groups[healthy].Owner);
        Assert.Equal(q, groups[strayOwner].Owner);
        Assert.Equal(r, groups[noOwner].Owner);
        Assert.Null(groups[abandoned].Owner);
        Assert.Null(groups[longEmpty].Owner);
        // Groups with members keep their code and its validity; the abandoned one stops being joinable now.
        Assert.True(groups[healthy].Expires > afterMigration.AddDays(4));
        Assert.True(groups[strayOwner].Expires > afterMigration.AddDays(4));
        Assert.InRange(groups[abandoned].Expires, beforeMigration.AddMinutes(-1), afterMigration.AddMinutes(1));
        Assert.Equal(new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), groups[longEmpty].Expires);
        Assert.Equal("ABANDON1", groups[abandoned].Code);

        // users.couple_id / couple_joined_at_utc are exactly as they were (they now mean "active group").
        var usersAfter = await database.RowsAsync("SELECT id, couple_id, couple_joined_at_utc FROM users ORDER BY id");
        Assert.Equal(usersBefore.Select(x => string.Join("|", x)), usersAfter.Select(x => string.Join("|", x)));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM notification_settings"));

        // The database itself refuses a second owner and a duplicate membership.
        var secondOwner = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            database.ExecuteAsync($"UPDATE couple_members SET role = 'Owner' WHERE user_id = '{older}'"));
        Assert.Equal("23505", secondOwner.SqlState);
        var duplicate = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO couple_members (couple_id, user_id, role, joined_at_utc) VALUES ('{healthy}', '{older}', 'Member', now())"));
        Assert.Equal("23505", duplicate.SqlState);

        // Through the application: the abandoned group's code is dead, the repaired owner manages their group.
        await using (var factory = new PostgresApiFactory(database))
        {
            var newcomer = await factory.RegisterAsync("Nova", createGroup: false);
            var join = await newcomer.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "ABANDON1" });
            Assert.Equal(HttpStatusCode.Gone, join.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await newcomer.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "STRAY123" })).StatusCode);
            // The join's own transaction is over when the group's oldest member is told about it.
            Assert.Equal(1, await database.ScalarAsync<long>(
                $"SELECT count(*) FROM notification_events WHERE alert_type = 'PartnerJoined' AND couple_id = '{strayOwner}' AND user_id = '{q}'"));
        }

        // A second group's preferences fit next to the first; then the way back keeps one row per user and the active group.
        await database.ExecuteAsync($"""
            INSERT INTO couple_members (couple_id, user_id, role, joined_at_utc) VALUES ('{noOwner}', '{owner}', 'Member', now());
            INSERT INTO notification_settings (id, bill_reminder_enabled, couple_id, large_transaction_enabled, low_balance_enabled, updated_at_utc, user_id) VALUES
              ('{Guid.NewGuid()}', false, '{noOwner}', true, true, '2026-10-01T12:00:00Z', '{owner}');
            """);

        await MigrateAsync(database, MigrationBeforeMemberships);

        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'couple_members'"));
        Assert.Equal(healthy, await database.ScalarAsync<Guid>($"SELECT couple_id FROM users WHERE id = '{owner}'"));
        Assert.Equal(healthy, await database.ScalarAsync<Guid>($"SELECT couple_id FROM notification_settings WHERE user_id = '{owner}'")); // the active group's row stays
        await MigrateAsync(database);
        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM couple_members")); // six from before plus the newcomer who joined
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
