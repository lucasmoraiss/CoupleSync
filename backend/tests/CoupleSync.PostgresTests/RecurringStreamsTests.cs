using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.AiFacts;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #39 on real PostgreSQL: the migration that adds recurring_streams and recurring_stream_items (additive, over
/// existing data), their columns and indexes, and what depends on the database — the unique index, the totals summed
/// by PostgreSQL, the group filter, a transaction deleted under a stream and two requests arriving together. The
/// clock of the API is injected.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RecurringStreamsTests
{
    // Named on purpose (never "the last one"): another delivery adding a migration must not change what is compared here.
    private const string MigrationBefore = "20261008185726_AddAiConsents";
    private const string ThisMigration = "20261008201834_AddRecurringStreams";
    private const string Recurring = "/api/v1/ai/recurring";

    // Noon in Brasília of 2026-10-08.
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0, DateTimeKind.Utc);

    private readonly PostgresServer _server;

    public RecurringStreamsTests(PostgresServer server) => _server = server;

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_OnlyAddsTheTwoTables_AndKeepsEveryExistingRowAndColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBefore);

        var existingTables = await TablesOfAsync(database);
        Assert.DoesNotContain("recurring_streams", existingTables);
        Assert.DoesNotContain("recurring_stream_items", existingTables);
        var countsBefore = new Dictionary<string, long>();
        foreach (var table in existingTables) countsBefore[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(countsBefore["transactions"] > 0 && countsBefore["users"] > 0, "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var columnsBefore = await ColumnsOfAsync(database, existingTables);
        var indexesBefore = await AllIndexesOfAsync(database, existingTables);

        await MigrationTests.MigrateAsync(database, ThisMigration);

        foreach (var table in existingTables)
            Assert.Equal(countsBefore[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(columnsBefore, await ColumnsOfAsync(database, existingTables));
        Assert.Equal(indexesBefore, await AllIndexesOfAsync(database, existingTables));

        Assert.Equal(["recurring_stream_items", "recurring_streams"], (await TablesOfAsync(database)).Except(existingTables).Order().ToList());
        // Nothing is calculated by the migration: the first request of each group does it.
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_streams"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_stream_items"));
    }

    [PostgresFact]
    public async Task TheMigration_CreatesTheColumnsAndTheIndexesOfTheDesign()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, ThisMigration);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = "uuid not null",
                ["couple_id"] = "uuid not null",
                ["merchant_key"] = "character varying not null",
                ["display_name"] = "character varying not null",
                ["kind"] = "character varying not null",
                ["variable_amount"] = "boolean not null",
                ["cadence"] = "character varying not null",
                ["category"] = "character varying not null",
                ["user_id"] = "uuid null",
                ["median_amount"] = "numeric not null",
                ["last_amount"] = "numeric not null",
                ["previous_amount"] = "numeric null",
                ["annual_cost"] = "numeric not null",
                ["occurrences"] = "integer not null",
                ["missed_count"] = "integer not null",
                ["first_seen_local"] = "date not null",
                ["last_seen_local"] = "date not null",
                ["next_expected_local"] = "date null",
                ["status"] = "character varying not null",
                ["flags"] = "character varying not null",
                ["confidence"] = "character varying not null",
                ["installment_number"] = "integer null",
                ["installment_total"] = "integer null",
                ["remaining_amount"] = "numeric null",
                ["end_month"] = "character varying null",
                ["user_override"] = "character varying null",
                ["override_by_user_id"] = "uuid null",
                ["override_at_utc"] = "timestamp with time zone null",
                ["detected_at_utc"] = "timestamp with time zone not null",
                ["updated_at_utc"] = "timestamp with time zone not null",
            }.OrderBy(c => c.Key),
            (await TypedColumnsOfAsync(database, "recurring_streams")).OrderBy(c => c.Key));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = "uuid not null",
                ["couple_id"] = "uuid not null",
                ["stream_id"] = "uuid not null",
                ["transaction_id"] = "uuid not null",
            }.OrderBy(c => c.Key),
            (await TypedColumnsOfAsync(database, "recurring_stream_items")).OrderBy(c => c.Key));

        var streamIndexes = await IndexesOfAsync(database, "recurring_streams");
        Assert.Contains(streamIndexes, i => i.Contains("UNIQUE") && i.Contains("(couple_id, merchant_key, kind, cadence)"));
        Assert.Contains(streamIndexes, i => !i.Contains("UNIQUE") && i.Contains("(couple_id, status)"));
        var itemIndexes = await IndexesOfAsync(database, "recurring_stream_items");
        Assert.Contains(itemIndexes, i => i.Contains("UNIQUE") && i.Contains("(stream_id, transaction_id)"));

        // An item goes away with its stream and with its transaction; the group is never deleted from under them.
        var rules = (await database.RowsAsync(
                """
                SELECT c.conrelid::regclass::text, c.confrelid::regclass::text, c.confdeltype::text
                FROM pg_constraint c
                WHERE c.contype = 'f' AND c.conrelid::regclass::text IN ('recurring_streams', 'recurring_stream_items')
                ORDER BY 1, 2
                """))
            .Select(r => $"{r[0]}->{r[1]}:{r[2]}")
            .ToList();
        Assert.Equal(
            ["recurring_stream_items->couples:r", "recurring_stream_items->recurring_streams:c", "recurring_stream_items->transactions:c", "recurring_streams->couples:r"],
            rules);
    }

    // ---------------------------------------------------------------- the unique index

    [PostgresFact]
    public async Task TheUniqueIndex_RefusesASecondStreamOfTheSameGroupKeyKindAndCadence_AndAcceptsItInAnotherGroup()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheClock(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        await SeedMonthlyAsync(ana.Client, "Streaming Exemplo", 39.90m, [8, 9, 10], "LAZER");
        await ListAsync(ana.Client);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_streams"));

        const string insert =
            """
            INSERT INTO recurring_streams (id, couple_id, merchant_key, display_name, kind, variable_amount, cadence, category, median_amount, last_amount,
                                           annual_cost, occurrences, missed_count, first_seen_local, last_seen_local, status, flags, confidence, detected_at_utc, updated_at_utc)
            VALUES (@id, @couple, 'streaming exemplo', 'Outro', @kind, false, @cadence, 'LAZER', 1, 1, 12, 3, 0, DATE '2026-08-05', DATE '2026-10-05', 'Active', '', 'High', now(), now())
            """;

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            insert, ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("kind", "Subscription"), ("cadence", "Monthly")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // Another kind, another cadence and another group are other streams.
        await database.ExecuteAsync(insert, ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("kind", "FixedBill"), ("cadence", "Monthly"));
        await database.ExecuteAsync(insert, ("id", Guid.NewGuid()), ("couple", ana.CoupleId!.Value), ("kind", "Subscription"), ("cadence", "Yearly"));
        await database.ExecuteAsync(insert, ("id", Guid.NewGuid()), ("couple", carla.CoupleId!.Value), ("kind", "Subscription"), ("cadence", "Monthly"));
        Assert.Equal(4, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_streams"));
    }

    [PostgresFact]
    public async Task TwoRequestsAtTheSameTime_BothAnswer200_AndLeaveOneRowPerStream()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheClock(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        await SeedMonthlyAsync(ana.Client, "Streaming Exemplo", 39.90m, [8, 9, 10], "LAZER");
        await SeedMonthlyAsync(ana.Client, "Clube Exemplo", 59.90m, [8, 9, 10], "LAZER");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ana.Client.GetAsync(Recurring)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_streams"));
        Assert.Equal(6, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_stream_items"));
        foreach (var response in responses)
            Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptions").GetArrayLength());
    }

    // ---------------------------------------------------------------- the totals, by PostgreSQL

    [PostgresFact]
    public async Task TheTotals_AreSummedByPostgres_OnlyOverTheActiveCommitmentsOfTheGroupOfTheToken()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheClock(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");

        await SeedMonthlyAsync(ana.Client, "Streaming Exemplo", 39.90m, [8, 9, 10], "LAZER");                 // subscription
        await SeedMonthlyAsync(ana.Client, "Clube Exemplo", 59.90m, [8, 9, 10], "LAZER");                     // subscription, will be hidden
        foreach (var (month, amount) in new[] { (8, 180m), (9, 230m), (10, 150m) })                           // variable bill: 180
            await ChargeAsync(ana.Client, "Companhia Exemplo", amount, Day(month, 6), "MORADIA");
        await ChargeAsync(ana.Client, "LOJA 02/10", 150m, Day(9, 12), "COMPRAS");                              // instalment 3/10
        await ChargeAsync(ana.Client, "LOJA 03/10", 150m, Day(10, 5), "COMPRAS");
        await ChargeAsync(ana.Client, "Seguro Exemplo", 1200m, new DateTime(2025, 9, 22, 15, 0, 0, DateTimeKind.Utc), "TRANSPORTE");   // yearly
        await ChargeAsync(ana.Client, "Seguro Exemplo", 1200m, Day(9, 22), "TRANSPORTE");
        for (var i = 0; i < 4; i++)                                                                            // weekly habit: not a commitment
            await ChargeAsync(ana.Client, "Feira Exemplo", 80m, Day(9, 15).AddDays(7 * i), "ALIMENTACAO");
        await ChargeAsync(ana.Client, "OUTRA LOJA 01/06", 70m, Day(10, 2), "COMPRAS");                         // probable instalment: not counted
        await SeedMonthlyAsync(carla.Client, "Streaming Exemplo", 999m, [8, 9, 10], "LAZER");                 // another group

        var first = await ListAsync(ana.Client);
        var clube = first.GetProperty("subscriptions").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Clube Exemplo");
        var patched = await ana.Client.PatchAsync(
            $"{Recurring}/{clube.GetProperty("id").GetGuid()}", JsonContent.Create(new Dictionary<string, string?> { ["override"] = "NotRecurring" }));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var list = await ListAsync(ana.Client);

        Assert.Equal(1, list.GetProperty("subscriptions").GetArrayLength());
        Assert.Equal(2, list.GetProperty("fixedBills").GetArrayLength());
        Assert.Equal(2, list.GetProperty("installments").GetArrayLength());
        Assert.Equal(1, list.GetProperty("habits").GetArrayLength());
        Assert.Equal(1, list.GetProperty("hidden").GetArrayLength());
        // 39.90 + 180 + 150 + 1200 / 12; in the year 478.80 + 2160 + 1050 (7 parts left) + 1200.
        Assert.Equal(39.90m + 180m + 150m + 100m, list.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(478.80m + 2160m + 1050m + 1200m, list.GetProperty("annualTotal").GetDecimal());

        // The same numbers straight from the rows, and the other group only sees its own.
        Assert.Equal(
            list.GetProperty("annualTotal").GetDecimal(),
            await database.ScalarAsync<decimal>(
                "SELECT sum(annual_cost) FROM recurring_streams WHERE couple_id = @couple AND kind <> 'Habit' AND user_override IS NULL AND confidence <> 'Low'",
                ("couple", ana.CoupleId!.Value)));
        var other = await ListAsync(carla.Client);
        Assert.Equal(999m, other.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(999m * 12, other.GetProperty("annualTotal").GetDecimal());

        // The dates are stored as dates, the amounts as numeric(18,2).
        Assert.Equal(
            new DateOnly(2026, 11, 5),
            DateOnly.FromDateTime(await database.ScalarAsync<DateTime>("SELECT next_expected_local::timestamp FROM recurring_streams WHERE display_name = 'Streaming Exemplo' AND couple_id = @couple", ("couple", ana.CoupleId!.Value))));
    }

    // ---------------------------------------------------------------- the group filter and deletions

    [PostgresFact]
    public async Task TheGlobalFilter_CoversBothTables_AndTheRoutesNeverCrossGroups()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheClock(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        await SeedMonthlyAsync(ana.Client, "Streaming Exemplo", 39.90m, [8, 9, 10], "LAZER");
        await SeedMonthlyAsync(carla.Client, "Clube Exemplo", 59.90m, [8, 9, 10], "LAZER");
        var anaId = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();
        await ListAsync(carla.Client);

        await using (var asAna = new AppDbContext(
                         new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, new FixedCoupleContext(ana.CoupleId)))
        {
            Assert.Equal("Streaming Exemplo", (await asAna.RecurringStreams.SingleAsync()).DisplayName);
            Assert.Equal(3, await asAna.RecurringStreamItems.CountAsync());
        }

        await using (var noGroup = factory.NewContext())
        {
            Assert.Equal(2, await noGroup.RecurringStreams.CountAsync());
            Assert.Equal(6, await noGroup.RecurringStreamItems.CountAsync());
        }

        var foreign = await carla.Client.GetAsync($"{Recurring}/{anaId}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("RECURRENCE_NOT_FOUND", (await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var patch = await carla.Client.PatchAsync($"{Recurring}/{anaId}", JsonContent.Create(new Dictionary<string, string?> { ["override"] = "Cancelled" }));
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_streams WHERE user_override IS NOT NULL"));
    }

    [PostgresFact]
    public async Task DeletingATransactionOfAStream_IsNotHeldBack_AndTakesItsItemWithIt()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithTheClock(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var ids = new List<Guid>();
        foreach (var month in new[] { 7, 8, 9, 10 })
            ids.Add(await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER"));
        Assert.Equal(1, (await ListAsync(ana.Client)).GetProperty("subscriptions").GetArrayLength());
        Assert.Equal(4, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_stream_items"));

        var deleted = await ana.Client.DeleteAsync($"/api/v1/transactions/{ids[0]}");

        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM recurring_stream_items"));
        // The next request sees one transaction less and calculates again: three charges, from August.
        var item = Assert.Single((await ListAsync(ana.Client)).GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(3, item.GetProperty("occurrences").GetInt32());
        Assert.Equal("2026-08-05", item.GetProperty("firstSeen").GetString());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The same API with the clock stopped at <see cref="Now"/>: "today" never comes from the machine.</summary>
    private static DerivedTestHost WithTheClock(PostgresApiFactory factory)
        => factory.WithTestHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDateTimeProvider>();
            services.AddSingleton<IDateTimeProvider>(new FixedClock(Now));
        }));

    private static DateTime Day(int month, int day) => new(2026, month, day, 15, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Recurring);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task SeedMonthlyAsync(HttpClient client, string merchant, decimal amount, int[] months, string category)
    {
        foreach (var month in months) await ChargeAsync(client, merchant, amount, Day(month, 5), category);
    }

    private static async Task<Guid> ChargeAsync(HttpClient client, string merchant, decimal amount, DateTime whenUtc, string category)
    {
        var response = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            Amount = amount,
            Currency = "BRL",
            EventTimestampUtc = whenUtc,
            Merchant = merchant,
            Category = category,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<string>> TablesOfAsync(TestDatabase database)
        => (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();

    private static async Task<Dictionary<string, string>> TypedColumnsOfAsync(TestDatabase database, string table)
        => (await database.RowsAsync(
                $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToDictionary(r => (string)r[0]!, r => $"{r[1]} {((string)r[2]! == "YES" ? "null" : "not null")}");

    private static async Task<List<string>> IndexesOfAsync(TestDatabase database, string table)
        => (await database.RowsAsync($"SELECT indexdef FROM pg_indexes WHERE tablename = '{table}' ORDER BY indexname"))
            .Select(r => (string)r[0]!)
            .ToList();

    private static async Task<List<string>> AllIndexesOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
        => (await database.RowsAsync("SELECT tablename, indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY tablename, indexname"))
            .Where(r => tables.Contains((string)r[0]!))
            .Select(r => (string)r[1]!)
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

    private sealed class FixedClock : IDateTimeProvider
    {
        public FixedClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; }
    }

    private sealed class FixedCoupleContext : ICoupleContext
    {
        public FixedCoupleContext(Guid? coupleId) => CoupleId = coupleId;

        public Guid? CoupleId { get; }
    }
}
