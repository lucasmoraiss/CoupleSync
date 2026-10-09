using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using Npgsql;
using static CoupleSync.PostgresTests.OpenFinanceSyncPostgresTests;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #27 on real PostgreSQL: the migration that adds <c>credit_card_bills</c> (additive, over data of the phases
/// before), <c>jsonb</c>, <c>date</c> and <c>numeric</c> columns, the unique bill, the exit from the group deleting
/// the bills, the payment of a bill recognised by its value, the entries that become incomes (also six at once, and
/// what the foreign key does when the income is deleted), and the group filter of the accounts and of the bills due.
/// Pluggy is <see cref="FakePluggyServer"/>; every name and value is invented.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenFinanceBillsPostgresTests
{
    // Named, never "the last one": migrations added later change other things.
    private const string MigrationBefore = "20261008195546_AddOpenFinanceReconciliation";
    private const string MigrationUnderTest = "20261009022052_AddCreditCardBills";
    private const string Base = "/api/v1/openfinance";
    private const string Checking = FakePluggyServer.CheckingAccountId;
    private const string Card = FakePluggyServer.CreditCardAccountId;
    private const string OtherChecking = FakePluggyServer.OtherCheckingAccountId;

    private readonly PostgresServer _server;

    public OpenFinanceBillsPostgresTests(PostgresServer server) => _server = server;

    private static readonly DateTime Today = DateTime.UtcNow.Date;

    /// <summary>Noon in Brasília, ten days ago.</summary>
    private static DateTime Noon() => Today.AddDays(-10).AddHours(15);

    private static DateOnly PaymentDay() => DateOnly.FromDateTime(Noon());

    private static DateOnly TodayInBrazil() => DateOnly.FromDateTime(BrazilTime.ToLocal(DateTime.UtcNow));

    private static FakeBill ClosedBill(string id, DateOnly due, decimal total)
        => new(id, due, total) { ClosingDate = due.AddDays(-7), MinimumPaymentAmount = Math.Round(total / 10, 2) };

    private static FakePluggyServer EmptyBanks()
    {
        var pluggy = new FakePluggyServer();
        foreach (var account in new[] { Checking, Card, OtherChecking }) pluggy.Transactions[account] = [];
        return pluggy;
    }

    private static async Task DoneAsync(TestDatabase database, TestUser user, Guid connection)
    {
        // The route lets a connection be synchronised once every ten minutes.
        await database.ExecuteAsync("UPDATE sync_runs SET created_at_utc = created_at_utc - interval '11 minutes'");
        var run = await SyncAsync(user, connection);
        Assert.True("Done" == run.GetProperty("status").GetString(), run.ToString());
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(HttpStatusCode.OK == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<Guid> LineAsync(TestDatabase database, string pluggyId)
        => database.ScalarAsync<Guid>("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", pluggyId));

    private static Task<string> StateAsync(TestDatabase database, string pluggyId)
        => database.ScalarAsync<string>(
            "SELECT review_state || ':' || coalesce(auto_reason, '') FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", pluggyId));

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_OnlyAddsTheTableOfTheBills_AndKeepsEveryExistingRowColumnAndConstraint()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBefore);
        // Data of the phases before, already in production: a connection with a card, lines in the mirror (one of
        // them an entry) and incomes.
        var couple = await database.ScalarAsync<Guid>("SELECT couple_id FROM transactions LIMIT 1");
        var user = await database.ScalarAsync<Guid>("SELECT user_id FROM couple_members WHERE couple_id = @couple LIMIT 1", ("couple", couple));
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, couple, user);
        var item = await OpenFinanceTests.InsertItemAsync(database, couple, connection, "item-before");
        var account = await OpenFinanceTests.InsertAccountAsync(database, couple, item, "account-before");
        await database.ExecuteAsync("UPDATE bank_accounts SET type = 'CREDIT', balance_due_date = '2026-10-27', credit_limit = 5000 WHERE id = @id", ("id", account));
        foreach (var (pluggy, amount, type, state) in new[] { ("old-debit", -10.00m, "Debit", "Pending"), ("old-credit", 3500.00m, "Credit", "Pending"), ("old-ignored", -700.00m, "Debit", "Ignored") })
        {
            await database.ExecuteAsync(
                """
                INSERT INTO bank_transactions (id, couple_id, user_id, bank_account_id, pluggy_transaction_id, date, local_date, amount, type, currency, status, review_state, raw_json, created_at_utc, updated_at_utc)
                VALUES (@id, @couple, @user, @account, @pluggy, '2026-10-05T15:00:00Z', '2026-10-05', @amount, @type, 'BRL', 'Posted', @state, '{"id":"x"}'::jsonb, now(), now())
                """,
                ("id", Guid.NewGuid()), ("couple", couple), ("user", user), ("account", account), ("pluggy", pluggy), ("amount", amount), ("type", type), ("state", state));
        }

        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'credit_card_bills'"));
        var tables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        var counts = new Dictionary<string, long>();
        foreach (var table in tables) counts[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(
            counts["transactions"] > 0 && counts["income_sources"] > 0 && counts["bank_transactions"] == 3 && counts["bank_accounts"] == 1,
            "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var incomeSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM income_sources");
        const string mirror = "SELECT pluggy_transaction_id, review_state, amount, type, local_date, raw_json::text, linked_income_source_id FROM bank_transactions ORDER BY pluggy_transaction_id";
        var mirrorBefore = (await database.RowsAsync(mirror)).Select(r => string.Join("|", r)).ToList();
        const string accounts = "SELECT id, type, balance, credit_limit, balance_due_date, sync_enabled FROM bank_accounts";
        var accountsBefore = (await database.RowsAsync(accounts)).Select(r => string.Join("|", r)).ToList();
        var columnsBefore = await ColumnsOfAsync(database, tables);
        var constraintsBefore = await ConstraintsOfAsync(database, tables);
        var indexesBefore = await IndexesOfAsync(database, tables);

        await MigrationTests.MigrateAsync(database, MigrationUnderTest);

        // No table loses a row, no money changes, no existing table changes shape.
        foreach (var table in tables)
            Assert.Equal(counts[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(incomeSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM income_sources"));
        Assert.Equal(mirrorBefore, (await database.RowsAsync(mirror)).Select(r => string.Join("|", r)).ToList());
        Assert.Equal(accountsBefore, (await database.RowsAsync(accounts)).Select(r => string.Join("|", r)).ToList());
        Assert.Equal(columnsBefore, await ColumnsOfAsync(database, tables));
        Assert.Equal(constraintsBefore, await ConstraintsOfAsync(database, tables));
        Assert.Equal(indexesBefore, await IndexesOfAsync(database, tables));

        // Exactly one table more, empty.
        var tablesAfter = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        Assert.Equal(new[] { "credit_card_bills" }, tablesAfter.Except(tables));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills"));

        // And a bill of the card that was already there can be stored.
        await InsertBillAsync(database, couple, account, "bill-after", new DateOnly(2026, 10, 27), 1500.25m);
        Assert.Equal(1500.25m, await database.ScalarAsync<decimal>("SELECT total_amount FROM credit_card_bills"));
    }

    [PostgresFact]
    public async Task TheMigration_CreatesTheColumnsWithTheTypesTheDesignAsksFor_AndCanBeRolledBackWhileEmpty()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, MigrationUnderTest);

        var columns = (await database.RowsAsync(
                "SELECT column_name, data_type, is_nullable, coalesce(numeric_precision::text, '') || ',' || coalesce(numeric_scale::text, '') FROM information_schema.columns WHERE table_name = 'credit_card_bills'"))
            .ToDictionary(r => (string)r[0]!, r => $"{r[1]} {((string)r[2]! == "YES" ? "null" : "not null")}");

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = "uuid not null",
                ["couple_id"] = "uuid not null",
                ["bank_account_id"] = "uuid not null",
                ["pluggy_bill_id"] = "character varying not null",
                ["due_date"] = "date not null",
                ["closing_date"] = "date null",
                ["total_amount"] = "numeric not null",
                ["currency"] = "character varying not null",
                ["minimum_payment"] = "numeric null",
                ["is_closed"] = "boolean not null",
                ["raw_json"] = "jsonb not null",
                ["created_at_utc"] = "timestamp with time zone not null",
                ["updated_at_utc"] = "timestamp with time zone not null",
            }.OrderBy(c => c.Key),
            columns.OrderBy(c => c.Key));
        Assert.Equal("18,2", await database.ScalarAsync<string>(
            "SELECT numeric_precision::text || ',' || numeric_scale::text FROM information_schema.columns WHERE table_name = 'credit_card_bills' AND column_name = 'total_amount'"));

        await MigrationTests.MigrateAsync(database, MigrationBefore);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'credit_card_bills'"));
        await MigrationTests.MigrateAsync(database, MigrationUnderTest);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'credit_card_bills'"));
    }

    [PostgresFact]
    public async Task TheDatabase_RefusesARepeatedPluggyBill_ABillWithoutItsAccount_AndDeletingAnAccountFromUnderItsBills()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno");
        var anaAccount = await SeedCardAsync(database, ana, "a");
        var brunoAccount = await SeedCardAsync(database, bruno, "b");

        // A Pluggy bill is mirrored once in the whole database, whatever the group or the card.
        await InsertBillAsync(database, ana.CoupleId!.Value, anaAccount, "bill-1", new DateOnly(2026, 10, 27), 100m);
        var repeated = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertBillAsync(database, bruno.CoupleId!.Value, brunoAccount, "bill-1", new DateOnly(2026, 10, 27), 100m));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, repeated.SqlState);
        Assert.Equal("IX_credit_card_bills_pluggy_bill_id", repeated.ConstraintName);

        var orphan = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertBillAsync(database, ana.CoupleId!.Value, Guid.NewGuid(), "bill-2", new DateOnly(2026, 10, 27), 100m));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
        var noGroup = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertBillAsync(database, Guid.NewGuid(), anaAccount, "bill-3", new DateOnly(2026, 10, 27), 100m));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, noGroup.SqlState);
        var held = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("DELETE FROM bank_accounts WHERE id = @id", ("id", anaAccount)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, held.SqlState);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills"));
    }

    // ---------------------------------------------------------------- the synchronisation

    [PostgresFact]
    public async Task TheBills_AreStoredAsJsonbDatesAndNumerics_SynchronisingAgainUpdatesTheSameRow_AndADebitOfTheTotalIsIgnoredAsBillPayment()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connection = await ConnectWithBankAsync(ana);
        var due = PaymentDay().AddDays(3);
        pluggy.Transactions[Checking] =
        [
            new FakeTransaction("t27-pays", Noon(), -1500.25m) { Description = "Débito automático" },
            new FakeTransaction("t27-same-value", Noon().AddHours(2), -1500.25m) { Description = "Outra coisa" },
            new FakeTransaction("t27-cent", Noon(), -1500.26m) { Description = "Quase" },
        ];
        pluggy.Bills[Card] = [new FakeBill("bill-27-pg", due, 1500.25m) { ClosingDate = due.AddDays(-9), MinimumPaymentAmount = 150.03m }];

        await DoneAsync(database, ana, connection);

        Assert.Equal("jsonb", await database.ScalarAsync<string>("SELECT pg_typeof(raw_json)::text FROM credit_card_bills"));
        Assert.Equal("bill-27-pg", await database.ScalarAsync<string>("SELECT raw_json->>'id' FROM credit_card_bills"));
        Assert.Equal(FakePluggyServer.RawOnlyMarker, await database.ScalarAsync<string>("SELECT raw_json->>'providerCode' FROM credit_card_bills"));
        Assert.Equal(1500.25m, await database.ScalarAsync<decimal>("SELECT total_amount FROM credit_card_bills"));
        Assert.Equal(150.03m, await database.ScalarAsync<decimal>("SELECT minimum_payment FROM credit_card_bills"));
        Assert.True(await database.ScalarAsync<bool>("SELECT is_closed FROM credit_card_bills"));
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM credit_card_bills WHERE due_date = @due AND closing_date = @closing AND currency = 'BRL'", ("due", due), ("closing", due.AddDays(-9))));
        Assert.Equal(
            await database.ScalarAsync<Guid>("SELECT id FROM bank_accounts WHERE pluggy_account_id = @id", ("id", Card)),
            await database.ScalarAsync<Guid>("SELECT bank_account_id FROM credit_card_bills"));

        // The debit of exactly the total is the payment (one per bill); numeric compares to the cent.
        Assert.Equal("Ignored:BillPayment", await StateAsync(database, "t27-pays"));
        Assert.Equal("Pending:", await StateAsync(database, "t27-same-value"));
        Assert.Equal("Pending:", await StateAsync(database, "t27-cent"));

        var id = await database.ScalarAsync<Guid>("SELECT id FROM credit_card_bills");
        pluggy.Bills[Card] = [new FakeBill("bill-27-pg", due.AddDays(1), 1620.00m) { ClosingDate = due.AddDays(-9) }];
        await DoneAsync(database, ana, connection);

        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills"));
        Assert.Equal(id, await database.ScalarAsync<Guid>("SELECT id FROM credit_card_bills"));
        Assert.Equal(1620.00m, await database.ScalarAsync<decimal>("SELECT total_amount FROM credit_card_bills"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills WHERE due_date = @due AND minimum_payment IS NULL", ("due", due.AddDays(1))));
        // What was already decided stays decided, and nothing else was ignored for the new total.
        Assert.Equal("Ignored:BillPayment", await StateAsync(database, "t27-pays"));
        Assert.Equal("Pending:", await StateAsync(database, "t27-same-value"));
    }

    [PostgresFact]
    public async Task ADebitThatArrivesAfterTheBill_AndADiscardInTheMiddleOfTheSave_AreHandledOnPostgres()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        var saves = new SaveHook();
        await using var host = WithOpenFinanceSync(factory, pluggy, database, saves);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connection = await ConnectWithBankAsync(ana);
        pluggy.Bills[Card] = [ClosedBill("bill-27-first", PaymentDay().AddDays(4), 880.40m)];
        await DoneAsync(database, ana, connection);

        // The bill is stored; the debit arrives now, in the account read before the card.
        pluggy.Transactions[Checking] =
        [
            new FakeTransaction("t27-later", Noon(), -880.40m) { Description = "PIX enviado" },
            new FakeTransaction("t27-raced", Noon(), -275.00m) { Description = "Outra conta" },
            new FakeTransaction("t27-not-raced", Noon(), -380.00m) { Description = "Mais uma conta" },
        ];
        await DoneAsync(database, ana, connection);
        Assert.Equal("Ignored:BillPayment", await StateAsync(database, "t27-later"));
        Assert.Equal("Pending:", await StateAsync(database, "t27-raced"));
        Assert.Equal("Pending:", await StateAsync(database, "t27-not-raced"));

        // Two more bills arrive, of the values of the debits that wait; while the run saves what it decided, the
        // person discards one of those debits: the review of the person stays, the run decides again about the
        // other one and ends well.
        pluggy.Bills[Card] =
        [
            ClosedBill("bill-27-first", PaymentDay().AddDays(4), 880.40m),
            ClosedBill("bill-27-second", PaymentDay().AddDays(5), 275.00m),
            ClosedBill("bill-27-third", PaymentDay().AddDays(5), 380.00m),
        ];
        var raced = await LineAsync(database, "t27-raced");
        var happened = 0;
        pluggy.BeforeAnswer = request =>
        {
            if (request.Path == "/bills" && Interlocked.Exchange(ref happened, 1) == 0)
            {
                // The first save after the bills are read stores the bill; the second one, what was decided.
                saves.BeforeNextSave = () =>
                {
                    saves.BeforeNextSave = () => database.ExecuteAsync(
                        "UPDATE bank_transactions SET review_state = 'Discarded' WHERE id = @id", ("id", raced));
                    return Task.CompletedTask;
                };
            }

            return Task.CompletedTask;
        };
        await DoneAsync(database, ana, connection);

        Assert.Equal(1, happened);
        Assert.Equal("Discarded:", await StateAsync(database, "t27-raced"));
        Assert.Equal("Ignored:BillPayment", await StateAsync(database, "t27-not-raced"));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills"));
    }

    // ---------------------------------------------------------------- who leaves takes the bills

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingOrBeingRemoved_DeletesTheBillsOfThePerson_AndOnlyThose(bool removedByTheOwner)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var brunoConnection = await ConnectWithBankAsync(bruno); // the card
        var anaConnection = await ConnectWithBankAsync(ana, itemId: FakePluggyServer.OtherItemWithAccounts);
        var today = TodayInBrazil();
        pluggy.Bills[Card] = [ClosedBill("bill-27-bruno-1", today.AddDays(5), 700m), ClosedBill("bill-27-bruno-2", today.AddDays(-25), 650m)];
        await DoneAsync(database, bruno, brunoConnection);
        await DoneAsync(database, ana, anaConnection);
        // A bill of someone who stays (stored straight in the table: the fixture has one card), and one of another group.
        var anaAccount = await database.ScalarAsync<Guid>("SELECT id FROM bank_accounts WHERE pluggy_account_id = @id", ("id", OtherChecking));
        await InsertBillAsync(database, ana.CoupleId!.Value, anaAccount, "bill-27-ana", today.AddDays(6), 90m);
        var carla = await factory.RegisterAsync("Carla");
        var carlaAccount = await SeedCardAsync(database, carla, "c");
        await InsertBillAsync(database, carla.CoupleId!.Value, carlaAccount, "bill-27-carla", today.AddDays(7), 80m);
        Assert.Equal(4, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills"));
        Assert.Equal(790m, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());

        Assert.Equal(
            new[] { "bill-27-ana", "bill-27-carla" },
            (await database.RowsAsync("SELECT pluggy_bill_id FROM credit_card_bills ORDER BY pluggy_bill_id")).Select(r => (string)r[0]!));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts WHERE pluggy_account_id = @id", ("id", Card)));
        Assert.Equal(90m, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());
        var people = (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/accounts")).GetProperty("people");
        Assert.Equal(ana.UserId, Assert.Single(people.EnumerateArray()).GetProperty("userId").GetGuid());
        // The same card can be connected by Bruno elsewhere afterwards, with its bills.
        var dora = await OpenFinanceTests.RegisterAsync(factory, host, "Dora");
        var doraConnection = await ConnectWithBankAsync(dora);
        await DoneAsync(database, dora, doraConnection);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills WHERE couple_id = @couple", ("couple", dora.CoupleId!.Value)));
    }

    // ---------------------------------------------------------------- accounts and bills due, by group

    [PostgresFact]
    public async Task TheAccountsAndTheBillsDue_AreOnlyTheOnesOfTheGroupOfTheToken_AndTheThirtyDaysAreReadFromTheDateColumn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connection = await ConnectWithBankAsync(ana);
        var today = TodayInBrazil();
        pluggy.Bills[Card] =
        [
            ClosedBill("bill-yesterday", today.AddDays(-1), 1000.00m),
            ClosedBill("bill-today", today, 100.10m),
            new FakeBill("bill-in-30", today.AddDays(30), 200.20m),
            new FakeBill("bill-in-31", today.AddDays(31), 4000.00m),
            ClosedBill("bill-dollars", today.AddDays(3), 50.00m) with { CurrencyCode = "USD" },
        ];
        await DoneAsync(database, ana, connection);
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla"); // another group
        var carlaAccount = await SeedCardAsync(database, carla, "c");
        await InsertBillAsync(database, carla.CoupleId!.Value, carlaAccount, "bill-carla", today.AddDays(2), 77777.00m);

        var anaRaw = await (await ana.Client.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();
        var upcoming = JsonSerializer.Deserialize<JsonElement>(anaRaw).GetProperty("upcomingBills");
        Assert.Equal(300.30m, upcoming.GetProperty("totalBrl").GetDecimal());
        Assert.Equal(
            new[] { (100.10m, "BRL"), (50.00m, "USD"), (200.20m, "BRL") },
            upcoming.GetProperty("bills").EnumerateArray().Select(b => (b.GetProperty("totalAmount").GetDecimal(), b.GetProperty("currency").GetString()!)));
        Assert.DoesNotContain("77777", anaRaw, StringComparison.Ordinal);

        var carlas = (await carla.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        Assert.Equal(77777.00m, carlas.GetProperty("totalBrl").GetDecimal());
        Assert.Single(carlas.GetProperty("bills").EnumerateArray());

        var anaAccounts = await (await ana.Client.GetAsync($"{Base}/accounts")).Content.ReadAsStringAsync();
        var card = Assert.Single(JsonSerializer.Deserialize<JsonElement>(anaAccounts).GetProperty("people")[0].GetProperty("cards").EnumerateArray());
        Assert.Equal(100.10m, card.GetProperty("currentBill").GetProperty("totalAmount").GetDecimal());
        Assert.Equal(3, card.GetProperty("nextBills").GetArrayLength());
        Assert.Equal(1000.00m, Assert.Single(card.GetProperty("previousBills").EnumerateArray()).GetProperty("totalAmount").GetDecimal());
        Assert.DoesNotContain("77777", anaAccounts, StringComparison.Ordinal);
        var carlaPerson = Assert.Single((await carla.Client.GetFromJsonAsync<JsonElement>($"{Base}/accounts")).GetProperty("people").EnumerateArray());
        Assert.Equal(carla.UserId, carlaPerson.GetProperty("userId").GetGuid());
        Assert.Equal(77777.00m, Assert.Single(carlaPerson.GetProperty("cards").EnumerateArray()).GetProperty("currentBill").GetProperty("totalAmount").GetDecimal());
    }

    [PostgresFact]
    public async Task TheBillsOfACardThePersonTurnedOff_AreNotDueNorShown_NorPayADebit_AndAreBackWhenTurnedOn_OnPostgres()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connection = await ConnectWithBankAsync(ana);
        var today = TodayInBrazil();
        pluggy.Bills[Card] = [ClosedBill("bill-off-due", today.AddDays(5), 700.00m), ClosedBill("bill-off-paid", PaymentDay().AddDays(4), 880.40m)];
        await DoneAsync(database, ana, connection);
        var cardId = await database.ScalarAsync<Guid>("SELECT id FROM bank_accounts WHERE pluggy_account_id = @id", ("id", Card));

        async Task<JsonElement> UpcomingAsync() => (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        async Task<JsonElement> CardAsync() => Assert.Single(
            (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/accounts")).GetProperty("people")[0].GetProperty("cards").EnumerateArray());
        async Task SetSyncAsync(bool enabled)
        {
            var answer = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{cardId}", new { syncEnabled = enabled });
            Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
        }

        Assert.Equal(700.00m, (await UpcomingAsync()).GetProperty("totalBrl").GetDecimal());

        await SetSyncAsync(false);

        var off = await UpcomingAsync();
        Assert.Equal(0m, off.GetProperty("totalBrl").GetDecimal());
        Assert.Empty(off.GetProperty("bills").EnumerateArray());
        var card = await CardAsync();
        Assert.Equal(JsonValueKind.Null, card.GetProperty("currentBill").ValueKind);
        Assert.Empty(card.GetProperty("nextBills").EnumerateArray());
        Assert.Empty(card.GetProperty("previousBills").EnumerateArray());
        // A debit of the value of a stored bill of that card arrives: nothing says the bill is still that one.
        pluggy.Transactions[Checking] = [new FakeTransaction("t27-off", Noon(), -880.40m) { Description = "PIX enviado" }];
        await DoneAsync(database, ana, connection);
        Assert.Equal("Pending:", await StateAsync(database, "t27-off"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM credit_card_bills"));

        await SetSyncAsync(true);

        Assert.Equal(700.00m, (await UpcomingAsync()).GetProperty("totalBrl").GetDecimal());
        Assert.Equal(700.00m, (await CardAsync()).GetProperty("currentBill").GetProperty("totalAmount").GetDecimal());
        Assert.Equal(880.40m, Assert.Single((await CardAsync()).GetProperty("previousBills").EnumerateArray()).GetProperty("totalAmount").GetDecimal());
    }

    // ---------------------------------------------------------------- entries that become incomes

    [PostgresFact]
    public async Task AnEntryBecomesAnIncomeOfTheMonthOfItsDayInBrazil_SixConfirmationsAtOnceCreateOne_AndDeletingItSendsTheLineBack()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connection = await ConnectWithBankAsync(ana);
        // 01:30 UTC of the first day of this month: 22:30 of the last day of the month before, in Brasília.
        var firstOfMonth = new DateTime(Today.Year, Today.Month, 1, 1, 30, 0, DateTimeKind.Utc);
        pluggy.Transactions[Checking] =
        [
            new FakeTransaction("t27-salary", Noon(), 3500.55m) { Type = "CREDIT", Description = "Pagamento Empresa Exemplo" },
            new FakeTransaction("t27-turn", firstOfMonth, 800.00m) { Type = "CREDIT", Description = "Pix da virada" },
        ];
        await DoneAsync(database, ana, connection);
        var salary = await LineAsync(database, "t27-salary");
        var turn = await LineAsync(database, "t27-turn");

        // The review reads the entries by the date column of the month in Brazil.
        var monthBefore = firstOfMonth.AddDays(-1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={monthBefore}");
        Assert.Contains(review.GetProperty("incomes").EnumerateArray(), l => l.GetProperty("id").GetGuid() == turn);
        Assert.Equal(2, review.GetProperty("pendingIncomesAllMonths").GetInt32());

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { incomes = new[] { new { id = salary }, new { id = turn } } })));

        // Each answer is "done" (registered here or already there) or "the review changed, refresh": never an error of the server.
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM income_sources"));
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM income_sources WHERE name = 'Pagamento Empresa Exemplo' AND amount = 3500.55 AND currency = 'BRL' AND is_shared AND NOT is_recurring AND user_id = @user AND month = @month",
            ("user", ana.UserId), ("month", BrazilTime.MonthOf(Noon()))));
        Assert.Equal(monthBefore, await database.ScalarAsync<string>("SELECT month FROM income_sources WHERE name = 'Pix da virada'"));
        Assert.Equal(
            await database.ScalarAsync<Guid>("SELECT id FROM income_sources WHERE name = 'Pagamento Empresa Exemplo'"),
            await database.ScalarAsync<Guid>("SELECT linked_income_source_id FROM bank_transactions WHERE id = @id", ("id", salary)));
        Assert.Equal("Confirmed:", await StateAsync(database, "t27-salary"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));

        // Deleting the income by its route sends the line back as discarded, without the link.
        var income = await database.ScalarAsync<Guid>("SELECT linked_income_source_id FROM bank_transactions WHERE id = @id", ("id", salary));
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/incomes/{income}")).StatusCode);
        Assert.Equal("Discarded:", await StateAsync(database, "t27-salary"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE id = @id AND linked_income_source_id IS NOT NULL", ("id", salary)));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM income_sources"));

        // Deleted straight in the table (not by the route): the foreign key clears the link, the line stays, and the
        // review treats it as discarded (it can be restored).
        await database.ExecuteAsync("DELETE FROM income_sources");
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_transactions WHERE id = @id AND review_state = 'Confirmed' AND linked_income_source_id IS NULL", ("id", turn)));
        review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={monthBefore}");
        Assert.Contains(review.GetProperty("discarded").EnumerateArray(), l => l.GetProperty("id").GetGuid() == turn);
        var restored = await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { turn }));
        Assert.Equal(new[] { turn }, restored.GetProperty("restored").EnumerateArray().Select(e => e.GetGuid()));
        var again = await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { incomes = new[] { new { id = turn, name = "Pix da virada" } } }));
        Assert.Single(again.GetProperty("incomes").EnumerateArray());
        // The incomes of the month mark it as come from the bank.
        var month = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{monthBefore}");
        Assert.True(Assert.Single(month.GetProperty("sharedIncome").GetProperty("sources").EnumerateArray()).GetProperty("fromBank").GetBoolean());
    }

    [PostgresFact]
    public async Task TwoEntriesWithTheNameOfAnIncomeThatAlreadyExists_GetAnotherName_InsteadOfBreakingTheUniqueIndexOfTheMonth()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyBanks();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var connection = await ConnectWithBankAsync(ana);
        pluggy.Transactions[Checking] =
        [
            new FakeTransaction("t27-pix-1", Noon(), 50.00m) { Type = "CREDIT", Description = "Pix recebido" },
            new FakeTransaction("t27-pix-2", Noon().AddHours(1), 60.00m) { Type = "CREDIT", Description = "Pix recebido" },
        ];
        await DoneAsync(database, ana, connection);
        var typed = await ana.Client.PostAsJsonAsync("/api/v1/incomes", new { month = BrazilTime.MonthOf(Noon()), name = "Pix recebido", amount = 5m, currency = "BRL", isShared = false });
        Assert.True(typed.IsSuccessStatusCode, await typed.Content.ReadAsStringAsync());

        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new
        {
            incomes = new[] { new { id = await LineAsync(database, "t27-pix-1") }, new { id = await LineAsync(database, "t27-pix-2") } },
        }));

        Assert.Equal(
            new[] { "Pix recebido", "Pix recebido (2)", "Pix recebido (3)" },
            (await database.RowsAsync("SELECT name FROM income_sources ORDER BY name")).Select(r => (string)r[0]!));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Guid> SeedCardAsync(TestDatabase database, TestUser user, string suffix)
    {
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, user.CoupleId!.Value, user.UserId);
        var item = await OpenFinanceTests.InsertItemAsync(database, user.CoupleId!.Value, connection, $"item-27-{suffix}");
        var account = await OpenFinanceTests.InsertAccountAsync(database, user.CoupleId!.Value, item, $"account-27-{suffix}");
        await database.ExecuteAsync("UPDATE bank_accounts SET type = 'CREDIT', name = 'Cartão Modelo' WHERE id = @id", ("id", account));
        return account;
    }

    private static Task InsertBillAsync(TestDatabase database, Guid couple, Guid account, string pluggyId, DateOnly due, decimal total)
        => database.ExecuteAsync(
            """
            INSERT INTO credit_card_bills (id, couple_id, bank_account_id, pluggy_bill_id, due_date, total_amount, currency, is_closed, raw_json, created_at_utc, updated_at_utc)
            VALUES (@id, @couple, @account, @pluggy, @due, @total, 'BRL', true, '{"id":"x"}'::jsonb, now(), now())
            """,
            ("id", Guid.NewGuid()), ("couple", couple), ("account", account), ("pluggy", pluggyId), ("due", due), ("total", total));

    private static async Task<List<string>> IndexesOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
    {
        var rows = await database.RowsAsync("SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY 1, 2");
        return rows.Where(r => tables.Contains((string)r[0]!)).Select(r => string.Join("|", r)).ToList();
    }
}
