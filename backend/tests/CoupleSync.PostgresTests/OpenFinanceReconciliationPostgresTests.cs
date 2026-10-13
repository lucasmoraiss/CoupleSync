using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.TestSupport;
using Microsoft.EntityFrameworkCore;
using static CoupleSync.PostgresTests.OpenFinanceSyncPostgresTests;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #26 on real PostgreSQL: the migration over lines that already exist, the search for a suspected duplicate
/// with the day counted in Brazil (<c>timestamptz</c> against <c>date</c>), the transfer between two people of the
/// group, "it is the same" under concurrency and what the foreign keys do when a transaction is deleted. Pluggy is
/// <see cref="FakePluggyServer"/>; every name and value is invented.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenFinanceReconciliationPostgresTests
{
    private const string MigrationBefore = "20261008185726_AddAiConsents";
    private const string Base = "/api/v1/openfinance";
    private const string Checking = FakePluggyServer.CheckingAccountId;
    private const string Card = FakePluggyServer.CreditCardAccountId;
    private const string OtherChecking = FakePluggyServer.OtherCheckingAccountId;

    private readonly PostgresServer _server;

    public OpenFinanceReconciliationPostgresTests(PostgresServer server) => _server = server;

    private static FakePluggyServer EmptyPluggy()
    {
        var pluggy = new FakePluggyServer();
        foreach (var account in new[] { Checking, Card, OtherChecking }) pluggy.Transactions[account] = [];
        return pluggy;
    }

    /// <summary>The day every date here is counted from, read once: a test that crosses midnight (UTC) still agrees with itself.</summary>
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    /// <summary>Noon in Brasília, some days ago.</summary>
    private static DateTime Noon(int daysAfterTheBase = 0) => Today.AddDays(-10 + daysAfterTheBase).AddHours(15);

    private static FakeTransaction Debit(string id, DateTime date, decimal amount, string description = "Compra Exemplo")
        => new(id, date, -amount) { Description = description };

    private static FakeTransaction Credit(string id, DateTime date, decimal amount)
        => new(id, date, amount) { Type = "CREDIT", Description = "Transferência recebida" };

    private static async Task<Guid> ManualAsync(TestUser user, decimal amount, DateTime whenUtc, string description = "Padaria")
    {
        var created = await user.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount,
            currency = "BRL",
            description,
            category = "OUTROS",
            eventTimestampUtc = whenUtc,
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(string State, string? Reason, Guid? Matched)> StateAsync(TestDatabase database, string pluggyId)
    {
        var row = Assert.Single(await database.RowsAsync(
            $"SELECT review_state, auto_reason, matched_transaction_id FROM bank_transactions WHERE pluggy_transaction_id = '{pluggyId}'"));
        return ((string)row[0]!, (string?)row[1], (Guid?)row[2]);
    }

    private static Task<Guid> LineAsync(TestDatabase database, string pluggyId)
        => database.ScalarAsync<Guid>("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = @id", ("id", pluggyId));

    private static async Task DoneAsync(TestDatabase database, TestUser user, Guid connectionId)
    {
        // Straight in the queue: the route would say it is too soon for a second synchronisation.
        var run = await InsertRunAsync(database, user, connectionId, "Pending");
        Assert.Equal("Done", (await WaitAsync(user, run)).GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_AddsOneNullableColumnAndTheForeignKeyOfTheSuspicion_OverLinesThatAlreadyExist_WithoutLosingAny()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBefore);
        // Data of phase 2 already in production: a connection with lines in the mirror, in every state of then.
        var couple = await database.ScalarAsync<Guid>("SELECT couple_id FROM transactions LIMIT 1");
        var user = await database.ScalarAsync<Guid>("SELECT user_id FROM couple_members WHERE couple_id = @couple LIMIT 1", ("couple", couple));
        var transaction = await database.ScalarAsync<Guid>("SELECT id FROM transactions WHERE couple_id = @couple LIMIT 1", ("couple", couple));
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, couple, user);
        var item = await OpenFinanceTests.InsertItemAsync(database, couple, connection, "item-before");
        var account = await OpenFinanceTests.InsertAccountAsync(database, couple, item, "account-before");
        await InsertOldLineAsync("old-pending", "Pending", linked: null, matched: null);
        await InsertOldLineAsync("old-confirmed", "Confirmed", linked: transaction, matched: null);
        await InsertOldLineAsync("old-discarded", "Discarded", linked: null, matched: null);
        // Nothing wrote the column before this phase; a stray value could only be one of these two.
        await InsertOldLineAsync("old-stray-valid", "Pending", linked: null, matched: transaction);
        await InsertOldLineAsync("old-stray-dangling", "Pending", linked: null, matched: Guid.NewGuid());

        var tables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        var counts = new Dictionary<string, long>();
        foreach (var table in tables) counts[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(counts["transactions"] > 0 && counts["bank_transactions"] == 5, "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var others = tables.Where(t => t != "bank_transactions").ToList();
        var columnsOfOthers = await ColumnsOfAsync(database, others);
        var constraintsOfOthers = await ConstraintsOfAsync(database, others);
        var columnsOfMirror = await ColumnsOfAsync(database, ["bank_transactions"]);
        var constraintsOfMirror = await ConstraintsOfAsync(database, ["bank_transactions"]);
        const string everyColumnOfBefore =
            "SELECT pluggy_transaction_id, review_state, linked_transaction_id, amount, date, local_date, raw_json::text, auto_reason, reviewed_at_utc FROM bank_transactions ORDER BY pluggy_transaction_id";
        var linesBefore = (await database.RowsAsync(everyColumnOfBefore)).Select(r => string.Join("|", r)).ToList();

        await MigrationTests.MigrateAsync(database);

        // No table loses a row, no money changes, no other table changes shape.
        foreach (var table in tables)
            Assert.Equal(counts[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(columnsOfOthers, await ColumnsOfAsync(database, others));
        Assert.Equal(constraintsOfOthers, await ConstraintsOfAsync(database, others));
        Assert.Equal(linesBefore, (await database.RowsAsync(everyColumnOfBefore)).Select(r => string.Join("|", r)).ToList());

        // The mirror gains exactly one column, nullable and empty, and exactly one constraint.
        var newColumn = Assert.Single((await ColumnsOfAsync(database, ["bank_transactions"])).Except(columnsOfMirror));
        Assert.Equal("bank_transactions|person_decided_at_utc|timestamp with time zone|YES|", newColumn);
        Assert.Empty(columnsOfMirror.Except(await ColumnsOfAsync(database, ["bank_transactions"])));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE person_decided_at_utc IS NOT NULL"));
        var newConstraint = Assert.Single((await ConstraintsOfAsync(database, ["bank_transactions"])).Except(constraintsOfMirror));
        Assert.Equal(
            "bank_transactions|FK_bank_transactions_transactions_matched_transaction_id|FOREIGN KEY (matched_transaction_id) REFERENCES transactions(id) ON DELETE SET NULL",
            newConstraint);
        Assert.Empty(constraintsOfMirror.Except(await ConstraintsOfAsync(database, ["bank_transactions"])));
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'bank_transactions' AND indexname = 'IX_bank_transactions_matched_transaction_id'"));

        // A suspicion that pointed to a transaction stays; one that pointed to nothing is cleared (the line stays).
        Assert.Equal(transaction, (await StateAsync(database, "old-stray-valid")).Matched);
        Assert.Equal(("Pending", null, null), await StateAsync(database, "old-stray-dangling"));

        await using (var db = MigrationTests.Context(database))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }

        // The new foreign key at work: without the transaction there is nothing left to suspect, and the line stays.
        await database.ExecuteAsync("UPDATE bank_transactions SET linked_transaction_id = NULL WHERE pluggy_transaction_id = 'old-confirmed'");
        await database.ExecuteAsync("DELETE FROM transactions WHERE id = @id", ("id", transaction));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "old-stray-valid"));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions"));

        Task InsertOldLineAsync(string pluggyId, string state, Guid? linked, Guid? matched)
            => database.ExecuteAsync(
                """
                INSERT INTO bank_transactions (id, couple_id, user_id, bank_account_id, pluggy_transaction_id, date, local_date, amount, type, currency, status,
                                               review_state, linked_transaction_id, matched_transaction_id, raw_json, created_at_utc, updated_at_utc)
                VALUES (@id, @couple, @user, @account, @pluggy, '2026-10-05T15:00:00Z', '2026-10-05', -10.00, 'Debit', 'BRL', 'Posted',
                        @state, @linked, @matched, '{"id":"x"}'::jsonb, now(), now())
                """,
                ("id", Guid.NewGuid()), ("couple", couple), ("user", user), ("account", account), ("pluggy", pluggyId), ("state", state),
                ("linked", (object?)linked ?? DBNull.Value), ("matched", (object?)matched ?? DBNull.Value));
    }

    // ---------------------------------------------------------------- the suspected duplicate, with the day of Brazil

    [PostgresFact]
    public async Task TheSuspectedDuplicate_IsFoundByTheDayInBrazil_OneDayApartAtMost_OnlyInTheGroup_ByEveryLineThatLooksLikeIt()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla Modelo");
        var connectionId = await ConnectWithBankAsync(ana);
        // The acceptance criterion: by hand on the 3rd, at the bank on the 4th (suspected) and on the 6th (not).
        var padaria = await ManualAsync(ana, 25.00m, Noon());
        await ManualAsync(ana, 40.00m, Noon(), "Mercado");
        // 22:00 in Brasília: stored as 01:00 UTC of the next day.
        var lateEvening = Today.AddDays(-9).AddHours(1);
        var dinner = await ManualAsync(ana, 31.00m, lateEvening, "Jantar");
        await ManualAsync(ana, 32.00m, lateEvening, "Bar");
        await ManualAsync(carla, 77.00m, Noon(), "De outro grupo");
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Café");
        pluggy.Transactions[Checking] =
        [
            Debit("t26-next-day", Noon(1), 25.00m),
            Debit("t26-three-days", Noon(3), 40.00m),
            // The day before in Brazil; two days before if the days were counted in UTC.
            Debit("t26-brazil-near", Noon(-1), 31.00m),
            // Two days after in Brazil; only one if the days were counted in UTC.
            Debit("t26-brazil-far", Noon(2), 32.00m),
            Debit("t26-other-group", Noon(), 77.00m),
            Debit("t26-coffee-1", Noon(), 9.00m),
            Debit("t26-coffee-2", Noon().AddHours(1), 9.00m),
        ];

        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());

        Assert.Equal(("Pending", null, padaria), await StateAsync(database, "t26-next-day"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-three-days"));
        Assert.Equal(("Pending", null, dinner), await StateAsync(database, "t26-brazil-near"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-brazil-far"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-other-group"));
        Assert.Equal(("Pending", null, coffee), await StateAsync(database, "t26-coffee-1"));
        // Two lines and one transaction: both are marked (only the person knows which is the one typed by hand).
        Assert.Equal(("Pending", null, coffee), await StateAsync(database, "t26-coffee-2"));

        // The review shows the day of the existing transaction as the day in Brazil, and only to its group.
        var line = await LineAsync(database, "t26-brazil-near");
        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon(-1));
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        var shown = review.GetProperty("expenses").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == line);
        Assert.Equal(dinner, shown.GetProperty("match").GetProperty("transactionId").GetGuid());
        Assert.Equal(
            Today.AddDays(-10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            shown.GetProperty("match").GetProperty("day").GetString());
        Assert.Equal(31.00m, shown.GetProperty("match").GetProperty("amount").GetDecimal());
        Assert.Empty((await carla.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}")).GetProperty("expenses").EnumerateArray());

        // A later synchronisation: what is only suspected is still there for another line; what is reconciled is not.
        pluggy.Transactions[Checking].Add(Debit("t26-coffee-3", Noon().AddHours(2), 9.00m));
        pluggy.Transactions[Checking].Add(Debit("t26-next-day-again", Noon(1).AddHours(1), 25.00m));
        (await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { reconcile = new[] { new { id = await LineAsync(database, "t26-next-day") } } })).EnsureSuccessStatusCode();
        await DoneAsync(database, ana, connectionId);
        Assert.Equal(("Pending", null, coffee), await StateAsync(database, "t26-coffee-3"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-next-day-again"));

        // The instants the search goes from and to, for lines of one single day: the first instant of the day before
        // and the last instant of the day after, both in Brazil.
        var day = Today.AddDays(-5);
        var firstOfTheDayBefore = await ManualAsync(ana, 51.00m, day.AddDays(-1).AddHours(3), "Primeiro instante do dia anterior");
        var lastOfTheDayAfter = await ManualAsync(ana, 52.00m, day.AddDays(2).AddHours(2).AddMinutes(59), "Último instante do dia seguinte");
        await ManualAsync(ana, 53.00m, day.AddDays(-1).AddHours(2).AddMinutes(59), "Um minuto antes");
        await ManualAsync(ana, 54.00m, day.AddDays(2).AddHours(3), "Um minuto depois");
        foreach (var (id, amount) in new[] { ("t26-from", 51.00m), ("t26-to", 52.00m), ("t26-before", 53.00m), ("t26-after", 54.00m) })
            pluggy.Transactions[Checking].Add(Debit(id, day.AddHours(15), amount));
        await DoneAsync(database, ana, connectionId);
        Assert.Equal(("Pending", null, firstOfTheDayBefore), await StateAsync(database, "t26-from"));
        Assert.Equal(("Pending", null, lastOfTheDayAfter), await StateAsync(database, "t26-to"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-before"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-after"));

        // Deleting the transaction clears the suspicion in the database itself; the line stays, waiting.
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{coffee}")).StatusCode);
        foreach (var id in new[] { "t26-coffee-1", "t26-coffee-2", "t26-coffee-3" })
            Assert.Equal(("Pending", null, null), await StateAsync(database, id));
    }

    [PostgresFact]
    public async Task AStatementLineImportedWithNoTime_StoredAtMidnightUtc_IsFoundAndComparedByTheDayWritten()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        // As the statement import stores a line: source OcrImport, bank "OCR Import", the date at midnight UTC (timestamptz).
        async Task<Guid> ImportedAsync(decimal amount, int daysAfterTheBase)
        {
            var id = await ManualAsync(ana, amount, Today.AddDays(-10 + daysAfterTheBase), "Linha do extrato");
            await database.ExecuteAsync("UPDATE transactions SET source = 1, bank = 'OCR Import' WHERE id = @id", ("id", id));
            Assert.Equal(
                Today.AddDays(-10 + daysAfterTheBase),
                (await database.ScalarAsync<DateTime>("SELECT event_timestamp_utc FROM transactions WHERE id = @id", ("id", id))).ToUniversalTime());
            return id;
        }

        var dayBefore = await ImportedAsync(61.00m, -1);
        var sameDay = await ImportedAsync(62.00m, 0);
        var dayAfter = await ImportedAsync(63.00m, 1);
        await ImportedAsync(64.00m, -2);
        await ImportedAsync(65.00m, 2);
        // An instant, not a date: 22:00 in Brasília of the day before the line, stored as 01:00Z of the day of the line.
        var lateEvening = await ManualAsync(ana, 66.00m, Today.AddDays(-10).AddHours(1), "Jantar");
        // Typed by hand as 21:00 in Brasília, two days before the line: stored as 00:00Z of the day before the line.
        // It is an instant too (the app never sends a date without a time): still two days apart.
        await ManualAsync(ana, 67.00m, Today.AddDays(-11), "Lanche das 21h");
        pluggy.Transactions[Checking] =
        [
            Debit("t26-statement-day-before", Noon(), 61.00m),
            Debit("t26-statement-same-day", Noon(), 62.00m),
            Debit("t26-statement-day-after", Noon(), 63.00m),
            Debit("t26-statement-two-before", Noon(), 64.00m),
            Debit("t26-statement-two-after", Noon(), 65.00m),
            Debit("t26-late-evening", Noon(), 66.00m),
            Debit("t26-typed-at-21h", Noon(), 67.00m),
        ];

        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());

        Assert.Equal(("Pending", null, dayBefore), await StateAsync(database, "t26-statement-day-before"));
        Assert.Equal(("Pending", null, sameDay), await StateAsync(database, "t26-statement-same-day"));
        Assert.Equal(("Pending", null, dayAfter), await StateAsync(database, "t26-statement-day-after"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-statement-two-before"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-statement-two-after"));
        Assert.Equal(("Pending", null, lateEvening), await StateAsync(database, "t26-late-evening"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-typed-at-21h"));

        // The review shows the day of the statement as written.
        var line = await LineAsync(database, "t26-statement-day-before");
        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon());
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        var shown = review.GetProperty("expenses").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == line);
        Assert.Equal(
            Today.AddDays(-11).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            shown.GetProperty("match").GetProperty("day").GetString());
    }

    // ---------------------------------------------------------------- transfers, bill payments and the decision of the person

    [PostgresFact]
    public async Task ATransferBetweenTwoPeopleOfTheGroup_IsIgnoredOnBothSides_ARestoredLineIsNeverIgnoredAgain_AndAnotherGroupIsNeverPaired()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno Silva", ana.JoinCode);
        var anaConnection = await ConnectWithBankAsync(ana);
        var brunoConnection = await ConnectWithBankAsync(bruno, itemId: FakePluggyServer.OtherItemWithAccounts);
        pluggy.Transactions[Checking] =
        [
            Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"),
            new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" },
            Debit("t26-usd-out", Noon(), 104.00m) with { CurrencyCode = "USD" },
            Credit("t26-from-bruno", Noon(), 300.00m) with { PayerName = "BRUNO EXEMPLO SILVA" },
            Credit("t26-from-company", Noon(), 310.00m) with { PayerName = "EMPRESA EXEMPLO LTDA" },
        ];
        pluggy.Transactions[OtherChecking] =
        [
            Credit("t26-received", Noon().AddHours(1), 500.00m),
            Credit("t26-usd-in", Noon(), 104.00m) with { CurrencyCode = "USD" },
        ];

        Assert.Equal("Done", (await SyncAsync(ana, anaConnection)).GetProperty("status").GetString());
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-sent"));
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(database, "t26-bill"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(database, "t26-from-bruno"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-from-company"));

        Assert.Equal("Done", (await SyncAsync(bruno, brunoConnection)).GetProperty("status").GetString());
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(database, "t26-sent"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(database, "t26-received"));
        // Another currency is never compared: not as a transfer, not as anything.
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-usd-out"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-usd-in"));

        // The ignored ones of the month, with the reason, for anyone of the group.
        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon());
        var review = await bruno.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        Assert.Equal(
            new[] { "BillPayment:Debit", "Transfer:Credit", "Transfer:Credit", "Transfer:Debit" },
            review.GetProperty("ignored").EnumerateArray()
                .Select(l => $"{l.GetProperty("autoReason").GetString()}:{l.GetProperty("type").GetString()}").Order(StringComparer.Ordinal));

        // Restored by the person: back to the review, and no later synchronisation ignores it again.
        var sent = await LineAsync(database, "t26-sent");
        var bill = await LineAsync(database, "t26-bill");
        var restored = await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { sent, bill });
        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE review_state = 'Pending' AND person_decided_at_utc IS NOT NULL AND auto_reason IS NULL AND reviewed_at_utc IS NULL"));
        pluggy.Transactions[OtherChecking].Add(Credit("t26-received-2", Noon().AddHours(2), 500.00m));
        await DoneAsync(database, ana, anaConnection);
        await DoneAsync(database, bruno, brunoConnection);
        await DoneAsync(database, ana, anaConnection);
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-sent"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-bill"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-received-2"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(database, "t26-received"));

        // Another group, with the same value on the same day: nothing of it is paired with this one, nor seen by it.
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla Modelo");
        var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(left.IsSuccessStatusCode, await left.Content.ReadAsStringAsync());
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE pluggy_transaction_id LIKE 't26-received%'"));
        var carlaConnection = await ConnectWithBankAsync(carla, itemId: FakePluggyServer.OtherItemWithAccounts);
        pluggy.Transactions[Checking].Add(Debit("t26-sent-later", Noon(1), 700.00m));
        pluggy.Transactions[OtherChecking] = [Credit("t26-carla-received", Noon(1), 700.00m)];
        Assert.Equal("Done", (await SyncAsync(carla, carlaConnection)).GetProperty("status").GetString());
        await DoneAsync(database, ana, anaConnection);
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-sent-later"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-carla-received"));
        Assert.Empty((await carla.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}")).GetProperty("ignored").EnumerateArray());
    }

    [PostgresFact]
    public async Task TheIgnoredOfAMonth_AreOnlyThoseOfThatMonth_ByTheDayInBrazil()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connection = await ConnectWithBankAsync(ana);
        // 35 days apart: never in the same month.
        var older = Noon(-35);
        pluggy.Transactions[Checking] =
        [
            new FakeTransaction("t26-bill-now", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" },
            new FakeTransaction("t26-bill-older", older, -800.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" },
        ];
        Assert.Equal("Done", (await SyncAsync(ana, connection)).GetProperty("status").GetString());
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(database, "t26-bill-now"));
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(database, "t26-bill-older"));
        var now = await LineAsync(database, "t26-bill-now");
        var before = await LineAsync(database, "t26-bill-older");
        var nowMonth = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon());
        var olderMonth = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(older);
        Assert.NotEqual(nowMonth, olderMonth);

        async Task<List<Guid>> IgnoredOfAsync(string month)
            => (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}"))
                .GetProperty("ignored").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(new[] { now }, await IgnoredOfAsync(nowMonth));
        Assert.Equal(new[] { before }, await IgnoredOfAsync(olderMonth));
    }

    [PostgresFact]
    public async Task ALegSomeoneDiscardsWhileTheRunIsPairingIt_StaysDiscarded_TheRunEndsWell_AndNothingIsWrittenOverTheReview()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        var saves = new SaveHook();
        await using var host = WithOpenFinanceSync(factory, pluggy, database, saves);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno Silva", ana.JoinCode);
        var anaConnection = await ConnectWithBankAsync(ana);
        var brunoConnection = await ConnectWithBankAsync(bruno, itemId: FakePluggyServer.OtherItemWithAccounts);
        pluggy.Transactions[Checking] = [Debit("t26-sent", Noon(), 500.00m)];
        pluggy.Transactions[OtherChecking] = [Credit("t26-received", Noon(), 500.00m)];
        Assert.Equal("Done", (await SyncAsync(ana, anaConnection)).GetProperty("status").GetString());
        var once = 0;
        pluggy.BeforeAnswer = request =>
        {
            // Bruno's run has the transactions of the account: its next save would ignore both legs.
            if (request.Path == FakePluggyServer.TransactionsPath && request.Query.Contains(OtherChecking, StringComparison.Ordinal)
                && Interlocked.Exchange(ref once, 1) == 0)
            {
                saves.BeforeNextSave = () => database.ExecuteAsync(
                    "UPDATE bank_transactions SET review_state = 'Discarded', reviewed_at_utc = now() WHERE pluggy_transaction_id = 't26-sent'");
            }

            return Task.CompletedTask;
        };

        Assert.Equal("Done", (await SyncAsync(bruno, brunoConnection)).GetProperty("status").GetString());

        Assert.Equal(1, once);
        Assert.Equal(("Discarded", null, null), await StateAsync(database, "t26-sent"));
        // The leg that arrived is not half a transfer: with nothing left to pair with, it waits like any other line.
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-received"));
    }

    [PostgresFact]
    public async Task ADiscardWhileTheRunSaves_UndoesNothingOfWhatTheRunDecidedAboutTheOtherLines_AndLeavesNoTransferByHalves()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        var saves = new SaveHook();
        await using var host = WithOpenFinanceSync(factory, pluggy, database, saves);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno Silva", ana.JoinCode);
        var anaConnection = await ConnectWithBankAsync(ana);
        var brunoConnection = await ConnectWithBankAsync(bruno, itemId: FakePluggyServer.OtherItemWithAccounts);
        var existing = await ManualAsync(ana, 25.00m, Noon());
        pluggy.Transactions[Checking] = [Debit("t26-sent-1", Noon(), 500.00m), Debit("t26-sent-2", Noon(), 600.00m)];
        // Bruno's bank has not settled his bill payment yet.
        pluggy.Transactions[OtherChecking] =
            [new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA", Status = "PENDING" }];
        Assert.Equal("Done", (await SyncAsync(ana, anaConnection)).GetProperty("status").GetString());
        Assert.Equal("Done", (await SyncAsync(bruno, brunoConnection)).GetProperty("status").GetString());
        var sent2 = await LineAsync(database, "t26-sent-2");
        // Now it has, and both transfers and a purchase Ana typed by hand arrive with it.
        pluggy.Transactions[OtherChecking] =
        [
            new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" },
            Credit("t26-received-1", Noon().AddHours(1), 500.00m),
            Credit("t26-received-2", Noon().AddHours(2), 600.00m),
            Debit("t26-padaria", Noon(1), 25.00m, "Padaria Exemplo"),
        ];
        var once = 0;
        pluggy.BeforeAnswer = request =>
        {
            // The run has the transactions of the account: right before it saves what it decided, Ana discards a leg.
            if (request.Path == FakePluggyServer.TransactionsPath && request.Query.Contains(OtherChecking, StringComparison.Ordinal)
                && Interlocked.Exchange(ref once, 1) == 0)
            {
                saves.BeforeNextSave = async () =>
                {
                    var discarded = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { sent2 } });
                    Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
                };
            }

            return Task.CompletedTask;
        };

        await DoneAsync(database, bruno, brunoConnection);

        Assert.Equal(1, once);
        // The transfer nobody touched is whole, on both sides.
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(database, "t26-sent-1"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(database, "t26-received-1"));
        // The leg Ana discarded is as she left it, and its other leg is not ignored alone.
        Assert.Equal(("Discarded", null, null), await StateAsync(database, "t26-sent-2"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-received-2"));
        // What the run decided about the lines nobody touched is kept: the one the bank has just settled and the new one.
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(database, "t26-bill"));
        Assert.Equal(("Pending", null, existing), await StateAsync(database, "t26-padaria"));
        Assert.Equal(
            "Posted",
            await database.ScalarAsync<string>("SELECT status FROM bank_transactions WHERE pluggy_transaction_id = 't26-bill'"));
    }

    [PostgresFact]
    public async Task APurchasePendingAtTheBank_SettledUnderAnotherId_IsStillASuspectedDuplicate_AndOneThatStaysKeepsItsOwn()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        var padaria = await ManualAsync(ana, 25.00m, Noon());
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Café");
        pluggy.Transactions[Checking] =
        [
            Debit("t26-authorised", Noon(1), 25.00m) with { Status = "PENDING" },
            Debit("t26-coffee-authorised", Noon(1), 9.00m) with { Status = "PENDING" },
        ];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        Assert.Equal(("Pending", null, padaria), await StateAsync(database, "t26-authorised"));
        Assert.Equal(("Pending", null, coffee), await StateAsync(database, "t26-coffee-authorised"));

        // The bank settles the first purchase under another id; the second is still pending, and another coffee arrives.
        pluggy.Transactions[Checking] =
        [
            Debit("t26-settled", Noon(1), 25.00m),
            Debit("t26-coffee-authorised", Noon(1), 9.00m) with { Status = "PENDING" },
            Debit("t26-coffee-another", Noon(1).AddHours(1), 9.00m),
        ];
        await DoneAsync(database, ana, connectionId);

        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE pluggy_transaction_id = 't26-authorised'"));
        // The line that left with this very reading does not hold the transaction any more...
        Assert.Equal(("Pending", null, padaria), await StateAsync(database, "t26-settled"));
        // ...and a line that stays still does.
        Assert.Equal(("Pending", null, coffee), await StateAsync(database, "t26-coffee-authorised"));
        // ...and another line of the same value is marked too: the suspicion of one line takes nothing from another.
        Assert.Equal(("Pending", null, coffee), await StateAsync(database, "t26-coffee-another"));
        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon(1));
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        Assert.Equal(3, review.GetProperty("suspectedDuplicates").GetInt32());
        var settled = await LineAsync(database, "t26-settled");
        var shown = review.GetProperty("expenses").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == settled);
        Assert.Equal(padaria, shown.GetProperty("match").GetProperty("transactionId").GetGuid());
    }

    [PostgresFact]
    public async Task TwoPurchasesOfTheSameValueOnFollowingDays_BothAlreadyInTheApp_EachBankLineKeepsThePurchaseOfItsDay_AndNothingEntersTwice()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        var first = await ManualAsync(ana, 30.00m, Noon(), "Almoço do primeiro dia");
        var second = await ManualAsync(ana, 30.00m, Noon(1), "Almoço do outro dia");
        // The first lunch is still pending at the bank.
        pluggy.Transactions[Checking] = [Debit("t26-lunch-1", Noon(), 30.00m) with { Status = "PENDING" }];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        Assert.Equal(("Pending", null, first), await StateAsync(database, "t26-lunch-1"));
        var lineOfFirst = await LineAsync(database, "t26-lunch-1");

        // The bank settles it under the same id; the second lunch and the payment of a bill arrive in the same reading.
        pluggy.Transactions[Checking] =
        [
            Debit("t26-lunch-1", Noon(), 30.00m),
            Debit("t26-lunch-2", Noon(1), 30.00m),
            new FakeTransaction("t26-bill", Noon(1), -900.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" },
        ];
        await DoneAsync(database, ana, connectionId);

        // The line judged again keeps the lunch of its day (the transaction it holds is still a candidate, for it
        // alone), and the lunch of the next day is of the line of the next day.
        Assert.Equal(lineOfFirst, await LineAsync(database, "t26-lunch-1"));
        Assert.Equal(("Pending", null, first), await StateAsync(database, "t26-lunch-1"));
        Assert.Equal(("Pending", null, second), await StateAsync(database, "t26-lunch-2"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(DISTINCT matched_transaction_id) FROM bank_transactions"));

        var review = new List<JsonElement>();
        foreach (var month in new[] { Noon(), Noon(1) }.Select(CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf).Distinct())
            review.Add(await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}"));
        var expenses = review.SelectMany(r => r.GetProperty("expenses").EnumerateArray()).ToList();
        // "Selecionar tudo" of the app takes only lines without a match: none here.
        Assert.Equal(2, expenses.Count);
        Assert.DoesNotContain(expenses, l => l.GetProperty("match").ValueKind == JsonValueKind.Null);
        // The bill payment is ignored, and counted for the whole group with its month (a date column grouped by month).
        Assert.All(review, r => Assert.Equal(1, r.GetProperty("ignoredAllMonths").GetInt32()));
        var ignoredMonth = Assert.Single(review[0].GetProperty("ignoredByMonth").EnumerateArray());
        Assert.Equal(CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon(1)), ignoredMonth.GetProperty("month").GetString());
        Assert.Equal(1, ignoredMonth.GetProperty("ignored").GetInt32());

        // "É a mesma" on both: the two purchases the app had, each with its line, and nothing new.
        foreach (var line in new[] { lineOfFirst, await LineAsync(database, "t26-lunch-2") })
        {
            var answered = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { reconcile = new[] { new { id = line } } });
            Assert.True(HttpStatusCode.OK == answered.StatusCode, await answered.Content.ReadAsStringAsync());
        }

        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(first, await database.ScalarAsync<Guid>("SELECT linked_transaction_id FROM bank_transactions WHERE pluggy_transaction_id = 't26-lunch-1'"));
        Assert.Equal(second, await database.ScalarAsync<Guid>("SELECT linked_transaction_id FROM bank_transactions WHERE pluggy_transaction_id = 't26-lunch-2'"));
    }

    // ---------------------------------------------------------------- every line that may be a duplicate is marked (review 9)

    private static async Task<HttpResponseMessage> AnswerAsync(TestUser user, object body)
        => await user.Client.PostAsJsonAsync($"{Base}/review/confirm", body);

    private static async Task OkAsync(TestUser user, object body)
    {
        var answered = await AnswerAsync(user, body);
        Assert.True(HttpStatusCode.OK == answered.StatusCode, await answered.Content.ReadAsStringAsync());
    }

    /// <summary>What "Selecionar tudo" of the app would send: the settled lines waiting that look like nothing the app has.</summary>
    private static async Task<List<Guid>> SelectAllAsync(TestUser user, params DateTime[] days)
    {
        var selected = new List<Guid>();
        foreach (var month in days.Select(CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf).Distinct())
        {
            var review = await user.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
            selected.AddRange(review.GetProperty("expenses").EnumerateArray()
                .Where(l => l.GetProperty("match").ValueKind == JsonValueKind.Null && l.GetProperty("bankStatus").GetString() == "Posted")
                .Select(l => l.GetProperty("id").GetGuid()));
        }

        return selected;
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeX2_AccountAndCardInTheSameRun_BothLinesAreMarked_AndItIsAnotherOneOnOneLeavesTheOtherMarked(bool duplicateInChecking)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        var fuel = await ManualAsync(ana, 50.00m, Noon(), "Posto Exemplo");
        var (duplicateAccount, otherAccount) = duplicateInChecking ? (Checking, Card) : (Card, Checking);
        pluggy.Transactions[duplicateAccount] = [Debit("px2-same-day", Noon().AddHours(1), 50.00m, "Posto Exemplo")];
        pluggy.Transactions[otherAccount] = [Debit("px2-next-day", Noon(1), 50.00m, "Outra compra")];

        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());

        Assert.Equal(("Pending", null, fuel), await StateAsync(database, "px2-same-day"));
        Assert.Equal(("Pending", null, fuel), await StateAsync(database, "px2-next-day"));
        Assert.Empty(await SelectAllAsync(ana, Noon(), Noon(1)));

        await OkAsync(ana, new { asNew = new[] { await LineAsync(database, "px2-next-day") } });

        Assert.Equal(("Pending", null, fuel), await StateAsync(database, "px2-same-day"));
        Assert.Empty(await SelectAllAsync(ana, Noon(), Noon(1)));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));

        await OkAsync(ana, new { reconcile = new[] { new { id = await LineAsync(database, "px2-same-day") } } });
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(fuel, await database.ScalarAsync<Guid>("SELECT linked_transaction_id FROM bank_transactions WHERE pluggy_transaction_id = 'px2-same-day'"));
    }

    [PostgresFact]
    public async Task ItIsTheSame_JudgesAgainInTheSameSaveEveryOtherLineThatPointedToTheTransaction_AndDeletingItJudgesThemToo()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        var first = await ManualAsync(ana, 9.00m, Noon(), "Café");
        pluggy.Transactions[Checking] = [Debit("t26-coffee-1", Noon(), 9.00m), Debit("t26-coffee-2", Noon().AddHours(2), 9.00m)];
        pluggy.Transactions[Card] = [Debit("t26-coffee-card", Noon(1), 9.00m)];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        foreach (var id in new[] { "t26-coffee-1", "t26-coffee-2", "t26-coffee-card" })
            Assert.Equal(("Pending", null, first), await StateAsync(database, id));
        // A second purchase of the same value, typed after the lines arrived, on the day of the card line.
        var second = await ManualAsync(ana, 9.00m, Noon(1), "Outro café");

        await OkAsync(ana, new { reconcile = new[] { new { id = await LineAsync(database, "t26-coffee-1") } } });

        // The others no longer point to the purchase that has its line: they point to the one still free.
        Assert.Equal(("Reconciled", null, null), await StateAsync(database, "t26-coffee-1"));
        Assert.Equal(("Pending", null, second), await StateAsync(database, "t26-coffee-2"));
        Assert.Equal(("Pending", null, second), await StateAsync(database, "t26-coffee-card"));
        Assert.Empty(await SelectAllAsync(ana, Noon(), Noon(1)));

        await OkAsync(ana, new { reconcile = new[] { new { id = await LineAsync(database, "t26-coffee-card") } } });

        // Nothing is left to look like: the last line waits as an ordinary expense, and "Selecionar tudo" takes it.
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-coffee-2"));
        Assert.Equal(new[] { await LineAsync(database, "t26-coffee-2") }, await SelectAllAsync(ana, Noon(), Noon(1)));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(DISTINCT linked_transaction_id) FROM bank_transactions WHERE linked_transaction_id IS NOT NULL"));

        // Deleting a transaction several lines point to: each is judged again right after the deletion.
        var third = await ManualAsync(ana, 14.00m, Noon(), "Lanche");
        var fourth = await ManualAsync(ana, 14.00m, Noon(1), "Lanche do outro dia");
        pluggy.Transactions[Checking].Add(Debit("t26-snack-1", Noon(), 14.00m));
        pluggy.Transactions[Checking].Add(Debit("t26-snack-2", Noon().AddHours(1), 14.00m));
        await DoneAsync(database, ana, connectionId);
        Assert.Equal(("Pending", null, third), await StateAsync(database, "t26-snack-1"));
        Assert.Equal(("Pending", null, third), await StateAsync(database, "t26-snack-2"));

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{third}")).StatusCode);

        Assert.Equal(("Pending", null, fourth), await StateAsync(database, "t26-snack-1"));
        Assert.Equal(("Pending", null, fourth), await StateAsync(database, "t26-snack-2"));
    }

    [PostgresFact]
    public async Task ItIsTheSame_AtOnceOnSixLinesThatPointToTheSameTransaction_LinksOneLineOnly_AndNoLineKeepsPointingToIt()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        var existing = await ManualAsync(ana, 9.00m, Noon(), "Café");
        var ids = Enumerable.Range(1, 6).Select(i => $"t26-coffee-{i}").ToList();
        pluggy.Transactions[Checking] = ids.Take(3).Select((id, i) => Debit(id, Noon().AddMinutes(i), 9.00m)).ToList();
        pluggy.Transactions[Card] = ids.Skip(3).Select((id, i) => Debit(id, Noon(1).AddMinutes(i), 9.00m)).ToList();
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        var lines = new List<Guid>();
        foreach (var id in ids)
        {
            Assert.Equal(("Pending", null, existing), await StateAsync(database, id));
            lines.Add(await LineAsync(database, id));
        }

        var answers = await Task.WhenAll(lines.Select(line => AnswerAsync(ana, new { reconcile = new[] { new { id = line } } })));

        // One wins; each of the others is told the review changed, or that its line no longer looks like anything.
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM bank_transactions WHERE linked_transaction_id = '{existing}'"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE review_state = 'Reconciled'"));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE review_state = 'Pending' AND matched_transaction_id IS NULL"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE matched_transaction_id IS NOT NULL"));

        // And a line that (by a synchronisation judging it at that very moment) still pointed to it is refused.
        var late = lines.First(line => answers[lines.IndexOf(line)].StatusCode != HttpStatusCode.OK);
        await database.ExecuteAsync($"UPDATE bank_transactions SET matched_transaction_id = '{existing}' WHERE id = '{late}'");
        var refused = await AnswerAsync(ana, new { reconcile = new[] { new { id = late } } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("MATCHED_TRANSACTION_TAKEN", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM bank_transactions WHERE linked_transaction_id = '{existing}'"));
    }

    // ---------------------------------------------------------------- "it is the same" and "it is another one"

    private async Task<(TestDatabase Database, PostgresApiFactory Factory, DerivedTestHost Host, TestUser Ana, Guid Existing, Guid Line)> WithDuplicateAsync(
        SaveHook? saves = null)
    {
        var database = await _server.CreateDatabaseAsync();
        var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        var host = saves is null ? WithOpenFinanceSync(factory, pluggy) : WithOpenFinanceSync(factory, pluggy, database, saves);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        var existing = await ManualAsync(ana, 25.00m, Noon());
        pluggy.Transactions[Checking] =
        [
            new FakeTransaction("t26-padaria", Noon(1), -25.00m)
            {
                Description = "Padaria Exemplo", MerchantName = "Padaria Exemplo Ltda", Category = "Groceries", CategoryId = "10000000",
            },
        ];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        return (database, factory, host, ana, existing, await LineAsync(database, "t26-padaria"));
    }

    [PostgresFact]
    public async Task ItIsTheSame_SixTimesAtOnce_ReconcilesOnce_CreatesNothing_AndFillsTheExistingTransaction()
    {
        var (database, factory, host, ana, existing, line) = await WithDuplicateAsync();
        await using var disposeDatabase = database;
        await using var disposeFactory = factory;
        await using var disposeHost = host;

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { reconcile = new[] { new { id = line } } })));

        // "Done" (here or already there) or "the review changed, refresh": never an error of the server.
        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        foreach (var ok in answers.Where(a => a.StatusCode == HttpStatusCode.OK))
        {
            var reconciled = Assert.Single((await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reconciled").EnumerateArray());
            Assert.Equal((line, existing), (reconciled.GetProperty("id").GetGuid(), reconciled.GetProperty("transactionId").GetGuid()));
        }

        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transaction_event_ingests"));
        var row = Assert.Single(await database.RowsAsync("SELECT review_state, linked_transaction_id, matched_transaction_id FROM bank_transactions"));
        Assert.Equal(("Reconciled", existing, null), ((string)row[0]!, (Guid?)row[1], (Guid?)row[2]));
        var transaction = Assert.Single(await database.RowsAsync("SELECT merchant, category, description, amount, source FROM transactions"));
        Assert.Equal(("Padaria Exemplo Ltda", "ALIMENTACAO", "Padaria", 25.00m), ((string?)transaction[0], (string?)transaction[1], (string?)transaction[2], (decimal)transaction[3]!));

        // Deleting the transaction the line was reconciled with: the line goes back as discarded, without the link.
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode);
        row = Assert.Single(await database.RowsAsync("SELECT review_state, linked_transaction_id, matched_transaction_id FROM bank_transactions"));
        Assert.Equal(("Discarded", null, null), ((string)row[0]!, (Guid?)row[1], (Guid?)row[2]));
    }

    [PostgresFact]
    public async Task ItIsTheSame_AnsweredWhileTheTransactionIsBeingDeleted_LeavesTheLineAsDiscarded_VisibleAndRestorable()
    {
        var saves = new SaveHook();
        var (database, factory, host, ana, existing, line) = await WithDuplicateAsync(saves);
        await using var disposeDatabase = database;
        await using var disposeFactory = factory;
        await using var disposeHost = host;
        // The deletion has read which lines point to the transaction (none yet) when the answer is stored; only then
        // is the transaction deleted, and the foreign key clears the link the line had just got.
        var answered = 0;
        saves.BeforeNextSave = async () =>
        {
            var answer = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { reconcile = new[] { new { id = line } } });
            Assert.True(HttpStatusCode.OK == answer.StatusCode, await answer.Content.ReadAsStringAsync());
            answered++;
        };

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode);

        Assert.Equal(1, answered);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        var row = Assert.Single(await database.RowsAsync("SELECT review_state, linked_transaction_id, matched_transaction_id FROM bank_transactions"));
        Assert.Equal(("Reconciled", null, null), ((string)row[0]!, (Guid?)row[1], (Guid?)row[2]));
        // Without its transaction the line is a discarded one: listed as such, never lost.
        var month = CoupleSync.Domain.ValueObjects.BrazilTime.MonthOf(Noon(1));
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        Assert.Equal(line, Assert.Single(review.GetProperty("discarded").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty(review.GetProperty("expenses").EnumerateArray());

        // And it comes back like any discarded line: restored (the person's from then on), then confirmed as an expense.
        var restored = await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line });
        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
        Assert.Equal(line, Assert.Single((await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restored").EnumerateArray()).GetGuid());
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_transactions WHERE review_state = 'Pending' AND person_decided_at_utc IS NOT NULL AND reviewed_at_utc IS NULL"));
        var confirmed = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = line } } });
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());
        var created = await database.ScalarAsync<Guid>("SELECT id FROM transactions");
        row = Assert.Single(await database.RowsAsync("SELECT review_state, linked_transaction_id, matched_transaction_id FROM bank_transactions"));
        Assert.Equal(("Confirmed", created, null), ((string)row[0]!, (Guid?)row[1], (Guid?)row[2]));
    }

    [PostgresFact]
    public async Task ItIsTheSame_AndDiscard_OfTheSameLineAtOnce_NeverLeaveAnythingByHalves()
    {
        var (database, factory, host, ana, existing, line) = await WithDuplicateAsync();
        await using var disposeDatabase = database;
        await using var disposeFactory = factory;
        await using var disposeHost = host;

        var answers = await Task.WhenAll(
            ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { reconcile = new[] { new { id = line } } }),
            ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { line } }));

        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        var row = Assert.Single(await database.RowsAsync("SELECT review_state, linked_transaction_id, matched_transaction_id FROM bank_transactions"));
        var merchant = await database.ScalarAsync<string?>("SELECT merchant FROM transactions");
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Null(row[2]);
        if ((string)row[0]! == "Reconciled")
        {
            // The reconciliation won whole: the link and what the transaction took from the bank.
            Assert.Equal((existing, "Padaria Exemplo Ltda"), ((Guid?)row[1], merchant));
        }
        else
        {
            // The discard won whole: no link, and the transaction got nothing from the bank.
            Assert.Equal(("Discarded", null, null), ((string)row[0]!, (Guid?)row[1], merchant));
        }
    }

    [PostgresFact]
    public async Task ItIsAnotherOne_SixTimesAtOnce_CreatesASingleTransaction_AndClearsTheSuspicion()
    {
        var (database, factory, host, ana, existing, line) = await WithDuplicateAsync();
        await using var disposeDatabase = database;
        await using var disposeFactory = factory;
        await using var disposeHost = host;

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { asNew = new[] { line } })));

        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        var created = await database.ScalarAsync<Guid>("SELECT id FROM transactions WHERE id <> @existing", ("existing", existing));
        var row = Assert.Single(await database.RowsAsync("SELECT review_state, linked_transaction_id, matched_transaction_id FROM bank_transactions"));
        Assert.Equal(("Confirmed", created, null), ((string)row[0]!, (Guid?)row[1], (Guid?)row[2]));
        // The one typed by hand got nothing from the bank.
        Assert.Null(await database.ScalarAsync<string?>("SELECT merchant FROM transactions WHERE id = @existing", ("existing", existing)));
    }

    // ---------------------------------------------------------------- the lines that already waited are judged at every reading (review 11)

    [PostgresFact]
    public async Task TheLinesThatAlreadyWaited_AreJudgedAgainByTheReadingOfTheirAccount_WhateverTheirAge_OnlyOfTheGroup_NeverTheOnesOfThePerson()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla Modelo");
        var anaConnection = await ConnectWithBankAsync(ana);
        var carlaConnection = await ConnectWithBankAsync(carla, itemId: FakePluggyServer.OtherItemWithAccounts);
        // The last line of the account is 7 days old: the next reading asks from 14 days ago on.
        var firstDayAsked = Today.AddDays(-14);
        // 23:00 in Brasília of the day before: 02:00 UTC of the first day asked. The bank lists it.
        var eveningBefore = firstDayAsked.AddHours(2);
        // Two months old: long before the days the next reading asks the bank for.
        var older = Today.AddDays(-60).AddHours(15);
        var existedBefore = await ManualAsync(ana, 26.00m, Noon(), "Já estava no app");
        pluggy.Transactions[Checking] =
        [
            Debit("t26-waiting", Noon(), 25.00m),
            Debit("t26-phase2", Noon(), 26.00m),
            Debit("t26-restored", Noon(), 27.00m),
            Debit("t26-evening-before", eveningBefore, 28.00m),
            Debit("t26-older", older, 29.00m),
            Debit("t26-last", Noon(3), 30.00m),
        ];
        pluggy.Transactions[Card] = [Debit("t26-card", Noon(), 31.00m)];
        pluggy.Transactions[OtherChecking] = [Debit("t26-carla", Noon(), 25.00m)];
        Assert.Equal("Done", (await SyncAsync(ana, anaConnection)).GetProperty("status").GetString());
        Assert.Equal("Done", (await SyncAsync(carla, carlaConnection)).GetProperty("status").GetString());
        Assert.Equal(("Pending", null, existedBefore), await StateAsync(database, "t26-phase2"));
        // As phase 2 (issue #25) stored it: waiting, settled, judged by nothing.
        await database.ExecuteAsync("UPDATE bank_transactions SET matched_transaction_id = NULL WHERE pluggy_transaction_id = 't26-phase2'");
        var restored = await LineAsync(database, "t26-restored");
        await OkAsync(ana, new { discard = new[] { restored } });
        (await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { restored })).EnsureSuccessStatusCode();
        // Typed after the lines arrived, one for each of them.
        var typed = await ManualAsync(ana, 25.00m, Noon(), "Digitada depois");
        await ManualAsync(ana, 27.00m, Noon(), "Igual à restaurada");
        var typedEvening = await ManualAsync(ana, 28.00m, eveningBefore, "Na noite anterior");
        var typedOlder = await ManualAsync(ana, 29.00m, older, "Antiga");
        var typedCard = await ManualAsync(ana, 31.00m, Noon(), "No cartão");
        var typedByCarla = await ManualAsync(carla, 25.00m, Noon(), "De outro grupo");

        // One reading of Ana's connection, in which the bank says nothing new about any of the lines.
        await DoneAsync(database, ana, anaConnection);

        Assert.Equal(firstDayAsked.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), pluggy.WindowsAsked(Checking)[^1].From);
        Assert.Equal(("Pending", null, typed), await StateAsync(database, "t26-waiting"));
        Assert.Equal(("Pending", null, existedBefore), await StateAsync(database, "t26-phase2"));
        // Its day in Brazil is the day before the first day asked (date against date, not against the instant in UTC).
        Assert.Equal(
            firstDayAsked.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            await database.ScalarAsync<string>("SELECT local_date::text FROM bank_transactions WHERE pluggy_transaction_id = 't26-evening-before'"));
        Assert.Equal(("Pending", null, typedEvening), await StateAsync(database, "t26-evening-before"));
        // The other account of the same connection is read, and judged, too.
        Assert.Equal(("Pending", null, typedCard), await StateAsync(database, "t26-card"));
        // Long before the days the bank was asked for: judged all the same (every line of the account that waits).
        Assert.Equal(("Pending", null, typedOlder), await StateAsync(database, "t26-older"));
        // The person brought it back: no synchronisation decides about it.
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-restored"));
        Assert.NotNull(await database.ScalarAsync<DateTime?>("SELECT person_decided_at_utc FROM bank_transactions WHERE pluggy_transaction_id = 't26-restored'"));
        // Another group: its line is not read by this run, and its transaction is nobody else's.
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-carla"));
        var selected = await SelectAllAsync(ana, Noon(), eveningBefore.AddDays(-1), older, Noon(3));
        Assert.Equal(
            new[] { await LineAsync(database, "t26-last"), restored }.Order(),
            selected.Order());

        // Carla's own reading marks her line, with her own transaction.
        await DoneAsync(database, carla, carlaConnection);
        Assert.Equal(("Pending", null, typedByCarla), await StateAsync(database, "t26-carla"));
        Assert.Equal(("Pending", null, typed), await StateAsync(database, "t26-waiting"));
    }

    // ---------------------------------------------------------------- the check at the moment of confirming (review 13)

    private static List<Guid> Ids(JsonElement body, string list)
        => body.GetProperty(list).EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Object ? e.GetProperty("id").GetGuid() : e.GetGuid())
            .ToList();

    [PostgresFact]
    public async Task Confirming_ChecksEachLineWithoutAMarkAgain_ByTheDayInBrazil_OnlyInTheGroup_AndStoresTheMarkTogetherWithTheExpenses_OrNothing()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        var saves = new SaveHook();
        await using var host = WithOpenFinanceSync(factory, pluggy, database, saves);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla Modelo");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = Today.AddDays(-10);
        pluggy.Transactions[Checking] =
        [
            // 20:00 in Brasília of the day (23:00 UTC of the same date).
            Debit("t26-evening", day.AddHours(23), 28.00m),
            // 23:00 in Brasília of the day BEFORE (02:00 UTC of the day).
            Debit("t26-night-before", day.AddHours(2), 29.00m),
            Debit("t26-plain", day.AddHours(15), 80.00m),
            Debit("t26-refused", day.AddHours(15), 31.00m),
            Debit("t26-refused-with", day.AddHours(15), 90.00m),
        ];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        // Typed after the run. 22:00 in Brasília of the NEXT day (01:00 UTC two dates later): one day apart in
        // Brazil, two dates apart in UTC.
        var typedNextEvening = await ManualAsync(ana, 28.00m, day.AddDays(2).AddHours(1), "No dia seguinte, à noite");
        // Noon of the next day: two days after the line of the night before in Brazil, one date apart in UTC.
        await ManualAsync(ana, 29.00m, day.AddDays(1).AddHours(15), "Dois dias depois");
        // The same value on the same day, in another group.
        await ManualAsync(carla, 80.00m, day.AddHours(15), "De outro grupo");
        var typedRefused = await ManualAsync(ana, 31.00m, day.AddHours(15), "Igual à da confirmação recusada");
        var evening = await LineAsync(database, "t26-evening");
        var nightBefore = await LineAsync(database, "t26-night-before");
        var plain = await LineAsync(database, "t26-plain");
        var refused = await LineAsync(database, "t26-refused");
        var refusedWith = await LineAsync(database, "t26-refused-with");
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM bank_transactions WHERE review_state = 'Pending' AND matched_transaction_id IS NULL"));

        var answered = await AnswerAsync(ana, new { expenses = new[] { new { id = evening }, new { id = nightBefore }, new { id = plain } } });

        Assert.True(HttpStatusCode.OK == answered.StatusCode, await answered.Content.ReadAsStringAsync());
        var body = await answered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { evening }, Ids(body, "heldAsPossibleDuplicates"));
        Assert.Equal(new[] { nightBefore, plain }.Order(), Ids(body, "created").Order());
        Assert.Equal(("Pending", null, typedNextEvening), await StateAsync(database, "t26-evening"));
        Assert.Equal("Confirmed", (await StateAsync(database, "t26-night-before")).State);
        Assert.Equal("Confirmed", (await StateAsync(database, "t26-plain")).State);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions t WHERE EXISTS (SELECT 1 FROM bank_transactions b WHERE b.linked_transaction_id = t.id)"));
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM transactions WHERE amount = 28.00 AND id <> '{typedNextEvening}'"));

        // One transaction of the database for the whole confirmation: when the save is refused (someone discards
        // another line of the request at that moment), the mark of the line held is not stored either.
        saves.BeforeNextSave = () => database.ExecuteAsync(
            "UPDATE bank_transactions SET review_state = 'Discarded', reviewed_at_utc = now() WHERE pluggy_transaction_id = 't26-refused-with'");
        var conflict = await AnswerAsync(ana, new { expenses = new[] { new { id = refused }, new { id = refusedWith } } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("BANK_REVIEW_CONFLICT", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-refused"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions t WHERE EXISTS (SELECT 1 FROM bank_transactions b WHERE b.linked_transaction_id = t.id)"));

        // Asked again, alone: held, and marked now.
        var again = await AnswerAsync(ana, new { expenses = new[] { new { id = refused } } });
        Assert.True(HttpStatusCode.OK == again.StatusCode, await again.Content.ReadAsStringAsync());
        Assert.Equal(new[] { refused }, Ids(await again.Content.ReadFromJsonAsync<JsonElement>(), "heldAsPossibleDuplicates"));
        Assert.Equal(("Pending", null, typedRefused), await StateAsync(database, "t26-refused"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions t WHERE EXISTS (SELECT 1 FROM bank_transactions b WHERE b.linked_transaction_id = t.id)"));
    }

    [PostgresFact]
    public async Task Confirming_SixTimesAtOnce_AsTheAppThatAsksToHoldPossibleDuplicates_ALineThatLooksLikeATransactionTypedAfter_CreatesNothing_AndLeavesTheLineMarked()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        pluggy.Transactions[Checking] = [Debit("t26-padaria", Noon(1), 25.00m)];
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        var typed = await ManualAsync(ana, 25.00m, Noon());
        var line = await LineAsync(database, "t26-padaria");

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AnswerAsync(ana, new { expenses = new[] { new { id = line } }, holdPossibleDuplicates = true })));

        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(("Pending", null, typed), await StateAsync(database, "t26-padaria"));
    }

    [PostgresFact]
    public async Task Confirming_AtOnceWithItIsTheSameOnAnotherLineOfTheSameTransaction_NeverRegistersThePurchaseTwice_AndNeverFailsAsTheServer()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var connectionId = await ConnectWithBankAsync(ana);
        // Twelve purchases typed by hand, each with two lines of the bank of its value: one marked (to be answered
        // "it is the same") and one as a line nothing judged yet (to be confirmed by "Selecionar tudo").
        const int rounds = 12;
        var existing = new List<Guid>();
        for (var i = 0; i < rounds; i++) existing.Add(await ManualAsync(ana, 10.00m + i, Noon(), $"Compra {i}"));
        pluggy.Transactions[Checking] = Enumerable.Range(0, rounds).Select(i => Debit($"t26-marked-{i:D2}", Noon().AddMinutes(i), 10.00m + i)).ToList();
        pluggy.Transactions[Card] = Enumerable.Range(0, rounds).Select(i => Debit($"t26-unjudged-{i:D2}", Noon().AddMinutes(i), 10.00m + i)).ToList();
        Assert.Equal("Done", (await SyncAsync(ana, connectionId)).GetProperty("status").GetString());
        await database.ExecuteAsync("UPDATE bank_transactions SET matched_transaction_id = NULL WHERE pluggy_transaction_id LIKE 't26-unjudged-%'");
        var marked = new List<Guid>();
        var unjudged = new List<Guid>();
        for (var i = 0; i < rounds; i++)
        {
            Assert.Equal(("Pending", null, existing[i]), await StateAsync(database, $"t26-marked-{i:D2}"));
            marked.Add(await LineAsync(database, $"t26-marked-{i:D2}"));
            unjudged.Add(await LineAsync(database, $"t26-unjudged-{i:D2}"));
        }

        var answers = await Task.WhenAll(Enumerable.Range(0, rounds).SelectMany(i => new[]
        {
            AnswerAsync(ana, new { reconcile = new[] { new { id = marked[i] } } }),
            AnswerAsync(ana, new { expenses = new[] { new { id = unjudged[i] } }, holdPossibleDuplicates = true }),
        }));

        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        for (var i = 0; i < rounds; i++)
        {
            var same = await StateAsync(database, $"t26-marked-{i:D2}");
            var other = await StateAsync(database, $"t26-unjudged-{i:D2}");
            var linked = await database.ScalarAsync<long>($"SELECT count(*) FROM bank_transactions WHERE linked_transaction_id = '{existing[i]}'");
            var ofTheValue = await database.ScalarAsync<long>("SELECT count(*) FROM transactions WHERE amount = @amount", ("amount", 10.00m + i));
            if (other.State == "Confirmed")
            {
                // It became another expense only because the purchase typed by hand already was the first line's.
                Assert.Equal(("Reconciled", 1L, 2L), (same.State, linked, ofTheValue));
            }
            else
            {
                // Held: nothing was created for it. It points to the purchase (even if that one was taken at the
                // very same moment: "É a mesma" on it is then refused) or, judged again by the answer, to nothing.
                Assert.Equal(("Pending", 1L), (other.State, ofTheValue));
                Assert.True(other.Matched is null || other.Matched == existing[i]);
                if (other.Matched is null) Assert.Equal("Reconciled", same.State);
            }

            Assert.True(linked <= 1);
        }

        // Whatever the order, every line can still be answered and no purchase ends up counted twice.
        for (var i = 0; i < rounds; i++)
        {
            if ((await StateAsync(database, $"t26-marked-{i:D2}")).State == "Pending")
                await OkAsync(ana, new { reconcile = new[] { new { id = marked[i] } } });
            if ((await StateAsync(database, $"t26-unjudged-{i:D2}")).State == "Pending")
                await OkAsync(ana, new { asNew = new[] { unjudged[i] } });
            Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM transactions WHERE amount = @amount", ("amount", 10.00m + i)));
            Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM bank_transactions WHERE linked_transaction_id = '{existing[i]}'"));
        }
    }

    // ---------------------------------------------------------------- every run judges the whole group first (review 13)

    [PostgresFact]
    public async Task ARun_JudgesFirstEveryWaitingLineOfTheGroup_AlsoOfTheAccountsItDoesNotRead_ByTheDayInBrazil_OnlyOfTheGroup_NeverTheOnesOfThePerson()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = EmptyPluggy();
        await using var host = WithOpenFinanceSync(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana Exemplo Souza");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno Silva", ana.JoinCode);
        var carla = await OpenFinanceTests.RegisterAsync(factory, host, "Carla Modelo");
        var anaConnection = await ConnectWithBankAsync(ana);
        var day = Today.AddDays(-10);
        pluggy.Transactions[Checking] =
        [
            // 20:00 in Brasília of the day (23:00 UTC of the same date).
            Debit("t26-evening", day.AddHours(23), 28.00m),
            // 23:00 in Brasília of the day BEFORE (02:00 UTC of the day).
            Debit("t26-night-before", day.AddHours(2), 29.00m),
            Debit("t26-restored", day.AddHours(15), 27.00m),
            Debit("t26-forty", day.AddHours(15), 40.00m),
        ];
        pluggy.Transactions[Card] = [Debit("t26-card", day.AddHours(15), 31.00m)];
        Assert.Equal("Done", (await SyncAsync(ana, anaConnection)).GetProperty("status").GetString());
        var restored = await LineAsync(database, "t26-restored");
        await OkAsync(ana, new { discard = new[] { restored } });
        (await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { restored })).EnsureSuccessStatusCode();
        // The checking account is no longer read; the card is.
        var checkingAccount = await database.ScalarAsync<Guid>("SELECT id FROM bank_accounts WHERE pluggy_account_id = @id", ("id", Checking));
        var off = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{checkingAccount}", new { syncEnabled = false });
        Assert.True(HttpStatusCode.OK == off.StatusCode, await off.Content.ReadAsStringAsync());
        // Typed after. 22:00 in Brasília of the NEXT day: one day apart in Brazil, two dates apart in UTC.
        var typedNextEvening = await ManualAsync(bruno, 28.00m, day.AddDays(2).AddHours(1), "No dia seguinte, à noite");
        // Noon of the next day: two days after the line of the night before in Brazil, one date apart in UTC.
        await ManualAsync(ana, 29.00m, day.AddDays(1).AddHours(15), "Dois dias depois");
        await ManualAsync(ana, 27.00m, day.AddHours(15), "Igual à restaurada");
        await ManualAsync(carla, 40.00m, day.AddHours(15), "De outro grupo");
        var typedCard = await ManualAsync(ana, 31.00m, day.AddHours(15), "No cartão");
        var asked = pluggy.WindowsAsked(Checking).Count;

        await DoneAsync(database, ana, anaConnection);

        Assert.Equal(asked, pluggy.WindowsAsked(Checking).Count);
        Assert.Equal(("Pending", null, typedNextEvening), await StateAsync(database, "t26-evening"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-night-before"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-restored"));
        Assert.Equal(("Pending", null, null), await StateAsync(database, "t26-forty"));
        Assert.Equal(("Pending", null, typedCard), await StateAsync(database, "t26-card"));
    }
}
