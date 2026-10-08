using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #25 on real PostgreSQL: the migration that adds sync_runs and bank_transactions (additive, over existing
/// data), <c>jsonb</c>, the unique indexes, the review by month of Brazil, the exit from the group deleting the mirror
/// and what happens when two things touch the same rows at once. Pluggy is <see cref="FakePluggyServer"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenFinanceSyncPostgresTests
{
    private const string MigrationBefore = "20261007205425_AddOpenFinanceConnections";
    private const string Base = "/api/v1/openfinance";

    private static readonly string[] NewTables = ["sync_runs", "bank_transactions"];

    private readonly PostgresServer _server;

    public OpenFinanceSyncPostgresTests(PostgresServer server) => _server = server;

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_OnlyAddsTheTwoTables_AndKeepsEveryExistingRowAndColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBefore);
        // Data of phase 1 already in production: a connection with its item and account.
        var couple = await database.ScalarAsync<Guid>("SELECT couple_id FROM couple_members LIMIT 1");
        var user = await database.ScalarAsync<Guid>("SELECT user_id FROM couple_members WHERE couple_id = @couple LIMIT 1", ("couple", couple));
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, couple, user);
        var item = await OpenFinanceTests.InsertItemAsync(database, couple, connection, "item-before");
        await OpenFinanceTests.InsertAccountAsync(database, couple, item, "account-before");

        foreach (var table in NewTables)
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{table}'"));
        var existingTables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        var countsBefore = new Dictionary<string, long>();
        foreach (var table in existingTables) countsBefore[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(countsBefore["transactions"] > 0 && countsBefore["bank_connections"] == 1, "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var sources = await database.RowsAsync("SELECT source, count(*) FROM transactions GROUP BY source ORDER BY source");
        var columnsBefore = await ColumnsOfAsync(database, existingTables);
        var constraintsBefore = await ConstraintsOfAsync(database, existingTables);

        await MigrationTests.MigrateAsync(database);

        foreach (var table in existingTables)
            Assert.Equal(countsBefore[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(
            sources.Select(r => $"{r[0]}:{r[1]}"),
            (await database.RowsAsync("SELECT source, count(*) FROM transactions GROUP BY source ORDER BY source")).Select(r => $"{r[0]}:{r[1]}"));
        Assert.Equal(columnsBefore, await ColumnsOfAsync(database, existingTables));
        Assert.Equal(constraintsBefore, await ConstraintsOfAsync(database, existingTables));

        foreach (var table in NewTables)
        {
            Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{table}'"));
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table}"));
        }

        await using var db = MigrationTests.Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheMigration_CreatesTheColumnsWithTheTypesTheDesignAsksFor()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);

        var columns = (await database.RowsAsync(
                "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name IN ('sync_runs','bank_transactions')"))
            .ToDictionary(r => $"{r[0]}.{r[1]}", r => $"{r[2]} {((string)r[3]! == "YES" ? "null" : "not null")}");

        Assert.Equal("jsonb not null", columns["bank_transactions.raw_json"]);
        Assert.Equal("date not null", columns["bank_transactions.local_date"]);
        Assert.Equal("timestamp with time zone not null", columns["bank_transactions.date"]);
        Assert.Equal("numeric not null", columns["bank_transactions.amount"]);
        Assert.Equal("numeric null", columns["bank_transactions.balance_after"]);
        Assert.Equal("uuid not null", columns["bank_transactions.couple_id"]);
        Assert.Equal("uuid not null", columns["bank_transactions.user_id"]);
        Assert.Equal("uuid not null", columns["bank_transactions.bank_account_id"]);
        Assert.Equal("character varying not null", columns["bank_transactions.pluggy_transaction_id"]);
        Assert.Equal("character varying not null", columns["bank_transactions.type"]);
        Assert.Equal("character varying not null", columns["bank_transactions.status"]);
        Assert.Equal("character varying not null", columns["bank_transactions.review_state"]);
        Assert.Equal("character varying not null", columns["bank_transactions.currency"]);
        Assert.Equal("uuid null", columns["bank_transactions.linked_transaction_id"]);
        Assert.Equal("uuid null", columns["bank_transactions.linked_income_source_id"]);
        Assert.Equal("uuid null", columns["bank_transactions.matched_transaction_id"]);
        Assert.Equal("uuid null", columns["bank_transactions.sync_run_id"]);
        Assert.Equal("character varying null", columns["bank_transactions.auto_reason"]);
        Assert.Equal("character varying null", columns["bank_transactions.suggested_category"]);
        Assert.Equal("timestamp with time zone null", columns["bank_transactions.reviewed_at_utc"]);
        Assert.Equal("integer null", columns["bank_transactions.installment_number"]);
        foreach (var text in new[] { "description", "description_raw", "pluggy_category", "pluggy_category_id", "merchant_name", "merchant_cnpj", "merchant_category", "payment_method", "bill_id" })
            Assert.Equal("character varying null", columns[$"bank_transactions.{text}"]);

        Assert.Equal("uuid not null", columns["sync_runs.connection_id"]);
        Assert.Equal("character varying not null", columns["sync_runs.status"]);
        Assert.Equal("character varying not null", columns["sync_runs.triggered_by"]);
        Assert.Equal("boolean not null", columns["sync_runs.force_item_update"]);
        Assert.Equal("boolean not null", columns["sync_runs.ai_categorization_consent"]);
        Assert.Equal("integer not null", columns["sync_runs.transactions_new"]);
        Assert.Equal("integer not null", columns["sync_runs.transactions_updated"]);
        Assert.Equal("timestamp with time zone null", columns["sync_runs.started_at_utc"]);
        Assert.Equal("timestamp with time zone null", columns["sync_runs.finished_at_utc"]);
        Assert.Equal("character varying null", columns["sync_runs.error_code"]);
        Assert.Equal("character varying null", columns["sync_runs.error_message"]);
        Assert.Equal("timestamp with time zone not null", columns["sync_runs.created_at_utc"]);

        await using var db = MigrationTests.Context(database);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // ---------------------------------------------------------------- what the database guarantees

    [PostgresFact]
    public async Task TheDatabase_RefusesARepeatedPluggyTransaction_AndASecondOpenRunOfAConnection()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno");
        var (connection, account) = await SeedBankAsync(database, ana, "a");
        var (otherConnection, otherAccount) = await SeedBankAsync(database, bruno, "b");

        // A Pluggy transaction is mirrored once in the whole database, whatever the group or the account.
        await InsertMirrorAsync(database, ana, account, "tx-1", new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc));
        var repeated = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertMirrorAsync(database, bruno, otherAccount, "tx-1", new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, repeated.SqlState);
        Assert.Equal("IX_bank_transactions_pluggy_transaction_id", repeated.ConstraintName);

        // One run waiting or running per connection; finished runs do not count, other connections are free.
        var first = await InsertRunAsync(database, ana, connection, "Pending");
        var second = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(database, ana, connection, "Pending"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);
        Assert.Equal("IX_sync_runs_one_open_per_connection", second.ConstraintName);
        await database.ExecuteAsync("UPDATE sync_runs SET status = 'Running' WHERE id = @id", ("id", first));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(database, ana, connection, "Pending"))).SqlState);
        await InsertRunAsync(database, bruno, otherConnection, "Pending");
        await database.ExecuteAsync("UPDATE sync_runs SET status = 'Done' WHERE id = @id", ("id", first));
        await InsertRunAsync(database, ana, connection, "Pending");
        await InsertRunAsync(database, ana, connection, "Failed");
        Assert.Equal(4, await database.ScalarAsync<long>("SELECT count(*) FROM sync_runs"));

        // The mirror needs its account, its run and its group to exist.
        var orphan = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertMirrorAsync(database, ana, Guid.NewGuid(), "tx-2", new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
        // An account with mirrored transactions cannot be deleted from under them.
        var held = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("DELETE FROM bank_accounts WHERE id = @id", ("id", account)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, held.SqlState);
    }

    [PostgresFact]
    public async Task TheRawJson_IsRealJsonb_AndDeletingTheLinkedTransactionOnlyClearsTheLink()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        var run = await SyncAsync(ana, connectionId);

        Assert.Equal("Done", run.GetProperty("status").GetString());
        Assert.Equal(5, run.GetProperty("transactionsNew").GetInt32());
        // jsonb: the raw transaction can be queried by its fields, with the ones the app does not read too.
        Assert.Equal("jsonb", await database.ScalarAsync<string>("SELECT pg_typeof(raw_json)::text FROM bank_transactions LIMIT 1"));
        Assert.Equal(5, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_transactions WHERE raw_json->>'providerCode' = @marker", ("marker", FakePluggyServer.RawOnlyMarker)));
        Assert.Equal(
            "Cantina Exemplo Ltda",
            await database.ScalarAsync<string>(
                "SELECT raw_json->'merchant'->>'name' FROM bank_transactions WHERE raw_json->>'id' = @id", ("id", FakePluggyServer.RestaurantTransactionId)));
        Assert.Equal(-58.90m, await database.ScalarAsync<decimal>(
            "SELECT (raw_json->>'amount')::numeric FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", FakePluggyServer.RestaurantTransactionId)));
        Assert.Equal(-58.90m, await database.ScalarAsync<decimal>(
            "SELECT amount FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", FakePluggyServer.RestaurantTransactionId)));

        // Synchronising again writes the same rows (a new run id), never new ones.
        await database.ExecuteAsync("UPDATE sync_runs SET created_at_utc = created_at_utc - interval '11 minutes'");
        var again = await SyncAsync(ana, connectionId);
        Assert.Equal((0, 5), (again.GetProperty("transactionsNew").GetInt32(), again.GetProperty("transactionsUpdated").GetInt32()));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE sync_run_id = @run", ("run", again.GetProperty("id").GetGuid())));

        // Confirm, then delete the transaction straight in the table (not by the route): the line stays, without the link.
        var line = await database.ScalarAsync<Guid>("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", FakePluggyServer.RestaurantTransactionId));
        var confirmed = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = line } } });
        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE linked_transaction_id IS NOT NULL"));
        Assert.Equal(3, await database.ScalarAsync<int>("SELECT source FROM transactions"));
        await database.ExecuteAsync("DELETE FROM transactions");
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE linked_transaction_id IS NOT NULL"));
    }

    // ---------------------------------------------------------------- the review by month of Brazil

    [PostgresFact]
    public async Task TheReview_GroupsByTheMonthOfBrazil_ReadFromTheDateColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 12);
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(DateTime.UtcNow));
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            // 01:30 UTC of the 1st is 22:30 of the last day of the month before in Brasília.
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c1", thisMonth.ToDateTime(new TimeOnly(1, 30), DateTimeKind.Utc), -10.00m) { Description = "Virada do mes" },
            // 03:00 UTC of the 1st is the first instant of this month in Brasília.
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c2", thisMonth.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc), -7.25m) { Description = "Primeiro instante" },
            // Midnight UTC of the 1st: a date without a time, the 1st as written.
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c3", thisMonth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), -3.00m) { Description = "Sem hora" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c4", lastMonth.AddDays(9).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), -20.50m) { Description = "Mes passado" },
        ];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());

        Assert.Equal(
            Text(thisMonth.AddDays(-1)),
            await database.ScalarAsync<string>("SELECT local_date::text FROM bank_transactions WHERE description = 'Virada do mes'"));
        Assert.Equal(Text(thisMonth), await database.ScalarAsync<string>("SELECT local_date::text FROM bank_transactions WHERE description = 'Sem hora'"));
        Assert.Equal(
            thisMonth.ToDateTime(new TimeOnly(1, 30), DateTimeKind.Utc),
            await database.ScalarAsync<DateTime>("SELECT date FROM bank_transactions WHERE description = 'Virada do mes'"));

        var lastKey = $"{lastMonth.Year:D4}-{lastMonth.Month:D2}";
        var thisKey = $"{thisMonth.Year:D4}-{thisMonth.Month:D2}";
        var previous = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={lastKey}");
        Assert.Equal(new[] { "Virada do mes", "Mes passado" }, previous.GetProperty("expenses").EnumerateArray().Select(l => l.GetProperty("description").GetString()));
        Assert.Equal(30.50m, previous.GetProperty("pendingTotalBrl").GetDecimal());
        Assert.Equal(Text(thisMonth.AddDays(-1)), previous.GetProperty("expenses")[0].GetProperty("day").GetString());

        var current = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={thisKey}");
        Assert.Equal(
            new[] { "Primeiro instante", "Sem hora" },
            current.GetProperty("expenses").EnumerateArray().Select(l => l.GetProperty("description").GetString()));
        Assert.Equal(10.25m, current.GetProperty("pendingTotalBrl").GetDecimal());
        Assert.Equal(4, current.GetProperty("pendingAllMonths").GetInt32());
        Assert.Equal(
            new[] { (thisKey, 2), (lastKey, 2) },
            current.GetProperty("pendingByMonth").EnumerateArray().Select(m => (m.GetProperty("month").GetString()!, m.GetProperty("pending").GetInt32())));

        // Another group sees none of it, and cannot confirm a line of this one.
        Assert.Equal(0, (await carla.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={lastKey}")).GetProperty("pendingAllMonths").GetInt32());
        var line = previous.GetProperty("expenses")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await carla.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = line } } })).StatusCode);

        // Confirmed: the transaction falls in the month of Brazil the review showed it in.
        (await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = line } } })).EnsureSuccessStatusCode();
        Assert.Equal(lastKey, BrazilTime.MonthOf(await database.ScalarAsync<DateTime>("SELECT event_timestamp_utc FROM transactions")));
    }

    [PostgresFact]
    public async Task TheGlobalFilter_ShowsEachGroupOnlyItsOwnRunsAndMirror()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno");
        var (anaConnection, anaAccount) = await SeedBankAsync(database, ana, "a");
        var (brunoConnection, brunoAccount) = await SeedBankAsync(database, bruno, "b");
        await InsertMirrorAsync(database, ana, anaAccount, "tx-ana", DateTime.UtcNow);
        await InsertMirrorAsync(database, bruno, brunoAccount, "tx-bruno", DateTime.UtcNow);
        await InsertRunAsync(database, ana, anaConnection, "Done");
        await InsertRunAsync(database, bruno, brunoConnection, "Done");

        await using (var asAna = ContextFor(database, ana.CoupleId))
        {
            Assert.Equal("tx-ana", Assert.Single(await asAna.BankTransactions.ToListAsync()).PluggyTransactionId);
            Assert.Equal(anaConnection, Assert.Single(await asAna.SyncRuns.ToListAsync()).ConnectionId);
        }

        await using var background = ContextFor(database, null);
        Assert.Equal(2, await background.BankTransactions.CountAsync());
        Assert.Equal(2, await background.SyncRuns.CountAsync());
    }

    // ---------------------------------------------------------------- two things at once

    [PostgresFact]
    public async Task ConfirmingTheSameLineSixTimesAtOnce_StoresASingleTransaction()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await SyncAsync(ana, connectionId);
        var line = await database.ScalarAsync<Guid>("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", FakePluggyServer.RestaurantTransactionId));

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = line } } })));

        // Each answer is "done" (created here or already there) or "the review changed, refresh": never an error of the server.
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transaction_event_ingests"));
        Assert.Equal(
            await database.ScalarAsync<Guid>("SELECT id FROM transactions"),
            await database.ScalarAsync<Guid>("SELECT linked_transaction_id FROM bank_transactions WHERE id = @id", ("id", line)));
        Assert.Equal("Confirmed", await database.ScalarAsync<string>("SELECT review_state FROM bank_transactions WHERE id = @id", ("id", line)));
    }

    [PostgresFact]
    public async Task AskingForASynchronisationSixTimesAtOnce_EnqueuesOne_TheOthersGet409()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        // Pluggy takes its time: the first run is still running while the others are asked for.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pluggy.BeforeAnswer = request => request.Path == "/transactions" ? release.Task : Task.CompletedTask;

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null)));
        release.SetResult();

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Accepted);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.Accepted), a => Assert.Equal(HttpStatusCode.Conflict, a.StatusCode));
        foreach (var refused in answers.Where(a => a.StatusCode == HttpStatusCode.Conflict))
            Assert.Equal("SYNC_ALREADY_RUNNING", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM sync_runs"));
        var accepted = await answers.Single(a => a.StatusCode == HttpStatusCode.Accepted).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Done", (await WaitAsync(ana, accepted.GetProperty("id").GetGuid())).GetProperty("status").GetString());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingOrBeingRemoved_DeletesTheMirrorAndTheRuns_KeepsWhatWasConfirmed_AndTheOthersOfTheGroup(bool removedByTheOwner)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var brunoConnection = await ConnectWithBankAsync(bruno);
        await SyncAsync(bruno, brunoConnection);
        var anaConnection = await ConnectWithBankAsync(ana, itemId: FakePluggyServer.OtherItemWithAccounts);
        await SyncAsync(ana, anaConnection);
        var line = await database.ScalarAsync<Guid>("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", FakePluggyServer.RestaurantTransactionId));
        (await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = line } } })).EnsureSuccessStatusCode();
        Assert.Equal(6, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());

        Assert.Equal(anaConnection, await database.ScalarAsync<Guid>("SELECT id FROM bank_connections"));
        Assert.Equal(anaConnection, await database.ScalarAsync<Guid>("SELECT connection_id FROM sync_runs"));
        Assert.Equal(FakePluggyServer.OtherAccountTransactionId, await database.ScalarAsync<string>("SELECT pluggy_transaction_id FROM bank_transactions"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
        // The expense already confirmed stays in the group, in the name of who had connected.
        Assert.Equal(bruno.UserId, await database.ScalarAsync<Guid>("SELECT user_id FROM transactions"));
        Assert.Equal(1, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review")).GetProperty("pendingAllMonths").GetInt32());
        // The same bank can be connected by Bruno in another group afterwards.
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla");
        var carlaConnection = await ConnectWithBankAsync(carla);
        Assert.Equal("Done", (await SyncAsync(carla, carlaConnection)).GetProperty("status").GetString());
        Assert.Equal(6, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingInTheMiddleOfARun_NeitherBlocksNorLeavesAnythingOfThePersonBehind(bool removedByTheOwner)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var connectionId = await ConnectWithBankAsync(bruno);
        // Pluggy holds the answer of the second account: the first is already in the mirror when the person leaves.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        pluggy.BeforeAnswer = async request =>
        {
            if (request.Path != "/transactions" || Interlocked.Increment(ref seen) != 2) return;
            reached.SetResult();
            await release.Task;
        };

        var accepted = await bruno.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions") > 0);

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());
        release.SetResult();

        // The run, which was still going, finds no connection to write to: it stops, and nothing comes back.
        var anaConnection = await ConnectWithBankAsync(ana, itemId: FakePluggyServer.OtherItemWithAccounts);
        Assert.Equal("Done", (await SyncAsync(ana, anaConnection)).GetProperty("status").GetString()); // the job is alive and well
        Assert.Equal(anaConnection, await database.ScalarAsync<Guid>("SELECT id FROM bank_connections"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM sync_runs"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE user_id = @user", ("user", bruno.UserId)));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_items WHERE pluggy_item_id = @item", ("item", FakePluggyServer.ItemWithAccounts)));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
    }

    [PostgresFact]
    public async Task DisconnectingInTheMiddleOfARun_TheRunStops_AndTheConnectionStaysDisconnected()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var once = 0;
        pluggy.BeforeAnswer = async request =>
        {
            if (request.Path != "/transactions" || Interlocked.Exchange(ref once, 1) == 1) return;
            Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        };

        var run = await SyncAsync(ana, connectionId);

        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Equal("BANK_CONNECTION_CHANGED", run.GetProperty("errorCode").GetString());
        var connection = Assert.Single(await database.RowsAsync(
            "SELECT status, client_id_encrypted, client_secret_encrypted, last_sync_at_utc, last_error_code FROM bank_connections"));
        Assert.Equal(new object?[] { "Disconnected", null, null, null, null }, connection);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));
    }

    [PostgresFact]
    public async Task ARunLeftRunning_IsFailedWhenTheApiStarts()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var (connection, _) = await SeedBankAsync(database, ana, "a");
        var stuck = await InsertRunAsync(database, ana, connection, "Running");

        await using var host = WithOpenFinanceSync(factory, new FakePluggyServer());
        using var client = host.CreateClient(); // starts the host, and with it the job

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await database.ScalarAsync<string>("SELECT status FROM sync_runs WHERE id = @id", ("id", stuck)) == "Running")
        {
            Assert.True(DateTime.UtcNow < deadline, "The stuck run was not failed in 30 s.");
            await Task.Delay(50);
        }

        var row = Assert.Single(await database.RowsAsync("SELECT status, error_code, error_message, finished_at_utc IS NOT NULL FROM sync_runs"));
        Assert.Equal("Failed", row[0]);
        Assert.Equal("SYNC_INTERRUPTED", row[1]);
        Assert.Equal("A sincronização foi interrompida porque o servidor reiniciou. Sincronize de novo.", row[2]);
        Assert.Equal(true, row[3]);
    }

    [PostgresFact]
    public async Task ARunRunningForMoreThan10Minutes_IsFailedByThePassOfTheJob_AndAYoungOneIsLeftAlone()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bia = await factory.RegisterAsync("Bia");
        var caio = await factory.RegisterAsync("Caio");
        var (anaConnection, _) = await SeedBankAsync(database, ana, "a");
        var (biaConnection, _) = await SeedBankAsync(database, bia, "b");
        var (caioConnection, _) = await SeedBankAsync(database, caio, "c");
        var davi = await factory.RegisterAsync("Davi");
        var (daviConnection, _) = await SeedBankAsync(database, davi, "d");
        var seenAtStart = await InsertRunAsync(database, davi, daviConnection, "Running");
        await using var host = WithOpenFinanceSync(factory, new FakePluggyServer());
        using var client = host.CreateClient(); // starts the host, and with it the job
        // The start of the job is over once the run that was already there got its verdict.
        var started = DateTime.UtcNow.AddSeconds(30);
        while (await database.ScalarAsync<string>("SELECT status FROM sync_runs WHERE id = @id", ("id", seenAtStart)) == "Running")
        {
            Assert.True(DateTime.UtcNow < started, "The job did not start in 30 s.");
            await Task.Delay(50);
        }

        // Left running by other processes AFTER this one started: its start never saw them.
        var old = await InsertRunAsync(database, ana, anaConnection, "Running");
        var oldWithoutStart = await InsertRunAsync(database, bia, biaConnection, "Running");
        var young = await InsertRunAsync(database, caio, caioConnection, "Running");
        await database.ExecuteAsync("UPDATE sync_runs SET started_at_utc = now() - interval '11 minutes' WHERE id = @id", ("id", old));
        await database.ExecuteAsync("UPDATE sync_runs SET created_at_utc = now() - interval '11 minutes' WHERE id = @id", ("id", oldWithoutStart));
        await database.ExecuteAsync("UPDATE sync_runs SET started_at_utc = now() - interval '9 minutes' WHERE id = @id", ("id", young));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await database.ScalarAsync<long>("SELECT count(*) FROM sync_runs WHERE status = 'Running'") > 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "The runs running for too long were not failed in 30 s.");
            await Task.Delay(50);
        }

        foreach (var id in new[] { old, oldWithoutStart })
        {
            var row = Assert.Single(await database.RowsAsync($"SELECT status, error_code, error_message, finished_at_utc IS NOT NULL FROM sync_runs WHERE id = '{id}'"));
            Assert.Equal("Failed", row[0]);
            Assert.Equal("SYNC_TIMED_OUT", row[1]);
            Assert.Equal("A sincronização demorou demais e foi encerrada. Sincronize de novo.", row[2]);
            Assert.Equal(true, row[3]);
        }

        await Task.Delay(300); // a few more passes
        Assert.Equal("Running", await database.ScalarAsync<string>("SELECT status FROM sync_runs WHERE id = @id", ("id", young)));
    }

    [PostgresFact]
    public async Task ALinePendingAtTheBankThatPluggyNoLongerLists_LeavesTheMirror_AndEachAccountIsAskedFromItsOwnLastTransaction()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));
        // Someone discarded the restaurant; the pending purchase of the card falls at the bank.
        await database.ExecuteAsync(
            "UPDATE bank_transactions SET review_state = 'Discarded' WHERE pluggy_transaction_id = @id", ("id", FakePluggyServer.RestaurantTransactionId));
        pluggy.Transactions[FakePluggyServer.CreditCardAccountId].RemoveAll(t => t.Id == FakePluggyServer.CardPendingTransactionId);
        pluggy.Transactions[FakePluggyServer.CheckingAccountId].RemoveAll(t => t.Id == FakePluggyServer.RestaurantTransactionId);
        var lastDayOfCard = DateOnly.ParseExact(
            await database.ScalarAsync<string>(
                """
                SELECT to_char(max(t.local_date), 'YYYY-MM-DD') FROM bank_transactions t JOIN bank_accounts a ON a.id = t.bank_account_id
                WHERE a.pluggy_account_id = @account
                """,
                ("account", FakePluggyServer.CreditCardAccountId)),
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);

        // The next run (straight in the queue: the route would say it is too soon).
        var second = await InsertRunAsync(database, ana, connectionId, "Pending");
        Assert.Equal("Done", (await WaitAsync(ana, second)).GetProperty("status").GetString());

        var left = (await database.RowsAsync("SELECT pluggy_transaction_id, review_state FROM bank_transactions"))
            .ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        Assert.DoesNotContain(FakePluggyServer.CardPendingTransactionId, left.Keys);
        // Settled and no longer listed: kept, as someone left it.
        Assert.Equal("Discarded", left[FakePluggyServer.RestaurantTransactionId]);
        Assert.Equal(4, left.Count);
        Assert.Equal(Text(lastDayOfCard.AddDays(-7)), pluggy.WindowsAsked(FakePluggyServer.CreditCardAccountId)[1].From);
    }

    // ---------------------------------------------------------------- support

    /// <summary>The API with a server key, the Pluggy client pointed at the fake and the synchronisation job looking at its queue every 50 ms.</summary>
    private static DerivedTestHost WithOpenFinanceSync(PostgresApiFactory factory, FakePluggyServer pluggy)
        => factory.WithTestHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENFINANCE_ENCRYPTION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["OpenFinance:PluggyBaseUrl"] = FakePluggyServer.BaseUrl,
                ["OpenFinance:SyncPollSeconds"] = "0.05",
                ["OpenFinance:SchedulerTickSeconds"] = "0",
                ["RateLimiting:OpenFinance:PermitLimit"] = "10000",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("Pluggy").ConfigurePrimaryHttpMessageHandler(() => pluggy);
                // The factory takes the job out of every host (a host without the key must not take runs): this one has it.
                services.AddHostedService<OpenFinanceSyncJob>();
            });
        });

    private static async Task<Guid> ConnectWithBankAsync(TestUser user, int historyMonths = 3, string itemId = FakePluggyServer.ItemWithAccounts)
    {
        var created = await user.Client.PostAsJsonAsync($"{Base}/connections", new
        {
            label = "Bancos",
            clientId = FakePluggyServer.ClientId,
            clientSecret = FakePluggyServer.ClientSecret,
            historyMonths,
        });
        Assert.True(HttpStatusCode.Created == created.StatusCode, await created.Content.ReadAsStringAsync());
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var added = await user.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId });
        Assert.True(HttpStatusCode.OK == added.StatusCode, await added.Content.ReadAsStringAsync());
        return connectionId;
    }

    private static async Task<JsonElement> SyncAsync(TestUser user, Guid connectionId)
    {
        var accepted = await user.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null);
        Assert.True(HttpStatusCode.Accepted == accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        return await WaitAsync(user, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> WaitAsync(TestUser user, Guid runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var run = await user.Client.GetFromJsonAsync<JsonElement>($"{Base}/sync-runs/{runId}");
            if (run.GetProperty("status").GetString() is "Done" or "Failed") return run;
            Assert.True(DateTime.UtcNow < deadline, "The run did not finish in 30 s.");
            await Task.Delay(25);
        }
    }

    private static async Task<(Guid Connection, Guid Account)> SeedBankAsync(TestDatabase database, TestUser user, string suffix)
    {
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, user.CoupleId!.Value, user.UserId);
        var item = await OpenFinanceTests.InsertItemAsync(database, user.CoupleId!.Value, connection, $"item-{suffix}");
        var account = await OpenFinanceTests.InsertAccountAsync(database, user.CoupleId!.Value, item, $"account-{suffix}");
        return (connection, account);
    }

    private static async Task<Guid> InsertRunAsync(TestDatabase database, TestUser user, Guid connection, string status)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO sync_runs (id, couple_id, connection_id, status, triggered_by, force_item_update, ai_categorization_consent, transactions_new, transactions_updated, created_at_utc)
            VALUES (@id, @couple, @connection, @status, 'User', false, false, 0, 0, now())
            """,
            ("id", id), ("couple", user.CoupleId!.Value), ("connection", connection), ("status", status));
        return id;
    }

    private static Task InsertMirrorAsync(TestDatabase database, TestUser user, Guid account, string pluggyId, DateTime date)
        => database.ExecuteAsync(
            """
            INSERT INTO bank_transactions (id, couple_id, user_id, bank_account_id, pluggy_transaction_id, date, local_date, amount, type, currency, status, review_state, raw_json, created_at_utc, updated_at_utc)
            VALUES (@id, @couple, @user, @account, @pluggy, @date, @day, -10.00, 'Debit', 'BRL', 'Posted', 'Pending', '{"id":"x"}'::jsonb, now(), now())
            """,
            ("id", Guid.NewGuid()), ("couple", user.CoupleId!.Value), ("user", user.UserId), ("account", account), ("pluggy", pluggyId),
            ("date", date), ("day", DateOnly.FromDateTime(date)));

    private static async Task<List<string>> ColumnsOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
    {
        var rows = await database.RowsAsync(
            "SELECT table_name, column_name, data_type, is_nullable, coalesce(column_default, '') FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, column_name");
        return rows.Where(r => tables.Contains((string)r[0]!)).Select(r => string.Join("|", r)).ToList();
    }

    private static async Task<List<string>> ConstraintsOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
    {
        var rows = await database.RowsAsync(
            "SELECT conrelid::regclass::text, conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE connamespace = 'public'::regnamespace ORDER BY 1, 2");
        return rows.Where(r => tables.Contains(((string)r[0]!).Trim('"'))).Select(r => string.Join("|", r)).ToList();
    }

    private static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static AppDbContext ContextFor(TestDatabase database, Guid? coupleId)
    {
        database.Server.Guard(database.ConnectionString);
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options,
            new FixedCoupleContext(coupleId));
    }

    private sealed class FixedCoupleContext : ICoupleContext
    {
        public FixedCoupleContext(Guid? coupleId) => CoupleId = coupleId;

        public Guid? CoupleId { get; }
    }
}
