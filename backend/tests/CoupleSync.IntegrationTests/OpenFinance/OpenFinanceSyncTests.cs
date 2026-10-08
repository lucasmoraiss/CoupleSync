using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #25 — the synchronisation: the hosted job takes a run from the queue (<c>sync_runs</c>), reads Pluggy
/// (<see cref="FakePluggyServer"/>, no network) and writes the mirror (<c>bank_transactions</c>). The runs are put in
/// the queue straight in the table; the routes that enqueue them are tested in <see cref="OpenFinanceReviewTests"/>.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceSyncTests
{
    private static OpenFinanceApiFactory NewFactory() => new() { FastSync = true };

    private static DateOnly BrazilDay(DateTime utc) => DateOnly.FromDateTime(BrazilTime.ToLocal(utc));

    private static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- the mirror

    [Fact]
    public async Task ARun_MirrorsEveryTransactionOfEveryAccount_WaitingForTheReview()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(5L, run["transactions_new"]);
        Assert.Equal(0L, run["transactions_updated"]);
        Assert.Null(run["error_code"]);
        Assert.NotNull(run["started_at_utc"]);
        Assert.NotNull(run["finished_at_utc"]);

        var mirror = await MirrorAsync(factory);
        Assert.Equal(5, mirror.Count);
        Assert.All(mirror, row =>
        {
            Assert.Equal("Pending", row["review_state"]);
            Assert.Equal(ana.CoupleId.ToString().ToUpperInvariant(), row["couple_id"]);
            Assert.Equal(ana.UserId.ToString().ToUpperInvariant(), row["user_id"]); // of who connected
            Assert.Equal(run["id"], row["sync_run_id"]);
            Assert.Null(row["linked_transaction_id"]);
            Assert.Contains(FakePluggyServer.RawOnlyMarker, (string)row["raw_json"]!, StringComparison.Ordinal);
        });

        var restaurant = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Debit", restaurant["type"]);
        Assert.Equal("Posted", restaurant["status"]);
        Assert.Equal("-58.9", Convert.ToString(restaurant["amount"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("BRL", restaurant["currency"]);
        Assert.Equal("Cantina Exemplo", restaurant["description"]);
        Assert.Equal("COMPRA DEBITO CANTINA EXEMPLO", restaurant["description_raw"]);
        Assert.Equal("Cantina Exemplo Ltda", restaurant["merchant_name"]);
        Assert.Equal(FakePluggyServer.MerchantCnpj, restaurant["merchant_cnpj"]);
        Assert.Equal("Eating out", restaurant["pluggy_category"]);
        Assert.Equal("11010000", restaurant["pluggy_category_id"]);
        Assert.Equal("ALIMENTACAO", restaurant["suggested_category"]);
        Assert.Equal(Text(BrazilDay(DateTime.UtcNow.Date.AddDays(-2).AddHours(15))), restaurant["local_date"]);

        Assert.Equal("TRANSPORTE", (await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId))["suggested_category"]);
        Assert.Equal("PIX", (await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId))["payment_method"]);

        // An entry is mirrored too (the review of entries is a later phase); it gets no expense category.
        var salary = await MirrorRowAsync(factory, FakePluggyServer.SalaryTransactionId);
        Assert.Equal("Credit", salary["type"]);
        Assert.Null(salary["suggested_category"]);

        var purchase = await MirrorRowAsync(factory, FakePluggyServer.CardPurchaseTransactionId);
        Assert.Equal("Debit", purchase["type"]); // a purchase on the card is positive at Pluggy, and still an expense
        Assert.Equal((2L, 6L), (purchase["installment_number"], purchase["installment_total"]));
        Assert.Equal("COMPRAS", purchase["suggested_category"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, FakePluggyServer.CardPendingTransactionId))["status"]);

        // Nothing became a transaction of the app: that is the review.
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));

        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.NotNull(connection["last_sync_at_utc"]);
        Assert.NotNull(connection["client_secret_encrypted"]);
    }

    [Fact]
    public async Task SynchronisingTheSameDaysTwice_DuplicatesNothing_UpdatesWhatPluggyChanged_AndKeepsTheReviewAndTheLink()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await SyncAsync(factory, ana, connectionId);

        // Between the two runs: one line was discarded and another confirmed (state and link written as the review does)...
        var transactionId = await SeedTransactionAsync(factory, ana);
        await factory.ExecuteAsync(
            "UPDATE bank_transactions SET review_state = 'Discarded', reviewed_at_utc = @now WHERE pluggy_transaction_id = @id",
            ("@now", DateTime.UtcNow), ("@id", FakePluggyServer.RideTransactionId));
        await factory.ExecuteAsync(
            "UPDATE bank_transactions SET review_state = 'Confirmed', linked_transaction_id = @tx, suggested_category = 'LAZER' WHERE pluggy_transaction_id = @id",
            ("@tx", transactionId), ("@id", FakePluggyServer.CardPendingTransactionId));
        // ...and the bank settled the pending purchase under another description and value, and renamed the ride.
        var card = factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId];
        card[1] = card[1] with { Status = "POSTED", Description = "Farmacia Exemplo Matriz", Amount = 47.50m, Category = "Shopping", CategoryId = "08000000" };
        var checking = factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId];
        checking[1] = checking[1] with { Description = "Corrida Exemplo Centro" };

        var second = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", second["status"]);
        Assert.Equal(0L, second["transactions_new"]);
        Assert.Equal(5L, second["transactions_updated"]);
        Assert.Equal(5, (await MirrorAsync(factory)).Count);

        var settled = await MirrorRowAsync(factory, FakePluggyServer.CardPendingTransactionId);
        Assert.Equal("Posted", settled["status"]);
        Assert.Equal("Farmacia Exemplo Matriz", settled["description"]);
        Assert.Equal("47.5", Convert.ToString(settled["amount"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("08000000", settled["pluggy_category_id"]);
        Assert.Equal(second["id"], settled["sync_run_id"]);
        // What the review decided stays exactly as it was.
        Assert.Equal("Confirmed", settled["review_state"]);
        Assert.Equal(transactionId.ToString().ToUpperInvariant(), settled["linked_transaction_id"]);
        Assert.Equal("LAZER", settled["suggested_category"]);

        var ride = await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId);
        Assert.Equal("Corrida Exemplo Centro", ride["description"]);
        Assert.Equal("Discarded", ride["review_state"]);
        Assert.NotNull(ride["reviewed_at_utc"]);
    }

    [Fact]
    public async Task TheFirstRun_AsksPluggyFromTodayMinusTheMonthsChosen_AndTheNextFromTheLastSynchronisationMinus7Days()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        var today = BrazilDay(factory.Clock.UtcNow);

        await SyncAsync(factory, ana, connectionId);

        var utcToday = Text(DateOnly.FromDateTime(factory.Clock.UtcNow));
        foreach (var account in new[] { FakePluggyServer.CheckingAccountId, FakePluggyServer.CreditCardAccountId })
        {
            var first = Assert.Single(factory.Pluggy.WindowsAsked(account));
            Assert.Equal(Text(today.AddMonths(-3)), first.From);
            Assert.Equal(utcToday, first.To);
        }

        // The last successful synchronisation was 10 days ago: the next run asks from 17 days ago.
        var lastSync = DateTime.UtcNow.AddDays(-10);
        await factory.ExecuteAsync("UPDATE bank_connections SET last_sync_at_utc = @at", ("@at", lastSync));

        await SyncAsync(factory, ana, connectionId);

        foreach (var account in new[] { FakePluggyServer.CheckingAccountId, FakePluggyServer.CreditCardAccountId })
        {
            var windows = factory.Pluggy.WindowsAsked(account);
            Assert.Equal(2, windows.Count);
            Assert.Equal(Text(BrazilDay(lastSync).AddDays(-7)), windows[1].From);
            Assert.Equal(utcToday, windows[1].To);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    public async Task TheFirstRun_GoesBackTheMonthsOfTheConnection(int historyMonths)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths);
        var old = DateTime.UtcNow.Date.AddMonths(-historyMonths).AddDays(3);
        var tooOld = DateTime.UtcNow.Date.AddMonths(-historyMonths).AddDays(-3);
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId].AddRange(
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000e1", old, -10m) { Description = "Dentro do periodo" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000e2", tooOld, -10m) { Description = "Antes do periodo" },
        ]);

        await SyncAsync(factory, ana, connectionId);

        Assert.Equal(Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-historyMonths)), factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId)[0].From);
        var ids = (await MirrorAsync(factory)).Select(r => (string)r["pluggy_transaction_id"]!).ToList();
        Assert.Contains("c1b2c3d4-0000-4000-8000-0000000000e1", ids);
        Assert.DoesNotContain("c1b2c3d4-0000-4000-8000-0000000000e2", ids);
    }

    [Fact]
    public async Task ABankAddedAfterTheFirstSynchronisation_GetsItsWholeHistoryToo()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 6);
        await SyncAsync(factory, ana, connectionId);
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);

        await SyncAsync(factory, ana, connectionId);

        var today = BrazilDay(factory.Clock.UtcNow);
        // The account never read before: the 6 months. The ones already read: 7 days before the earlier of the last
        // synchronisation (today) and their own last transaction (2 days ago).
        Assert.Equal(Text(today.AddMonths(-6)), Assert.Single(factory.Pluggy.WindowsAsked(FakePluggyServer.OtherCheckingAccountId)).From);
        Assert.Equal(
            Text(BrazilDay(DateTime.UtcNow.Date.AddDays(-2).AddHours(15)).AddDays(-7)),
            factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId)[1].From);
        Assert.Equal(6, (await MirrorAsync(factory)).Count);
        Assert.Equal("ALIMENTACAO", (await MirrorRowAsync(factory, FakePluggyServer.OtherAccountTransactionId))["suggested_category"]);
    }

    [Theory]
    // The app already installed sends 3 with the new credentials, whatever was chosen before; the new one sends nothing.
    [InlineData(3)]
    [InlineData(null)]
    public async Task ConnectingAgain_AConnectionAlreadySynchronised_KeepsThePeriodChosen_AndABankAddedLaterIsAskedThatPeriod(int? periodSent)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 12);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        // "Desconectar" and "Conectar de novo" (the way out of a connection with error).
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        var body = new Dictionary<string, object?>
        {
            ["label"] = "Bancos da Ana",
            ["clientId"] = FakePluggyServer.ClientId,
            ["clientSecret"] = FakePluggyServer.ClientSecret,
        };
        if (periodSent is { } sent) body["historyMonths"] = sent;
        var again = await ana.Client.PostAsJsonAsync($"{Base}/connections", body);

        Assert.True(HttpStatusCode.Created == again.StatusCode, await again.Content.ReadAsStringAsync());
        var connection = await JsonAsync(again);
        Assert.Equal(connectionId, connection.GetProperty("id").GetGuid());
        Assert.Equal(12, connection.GetProperty("historyMonths").GetInt32());
        Assert.Equal(12L, Assert.Single(await factory.RowsAsync("SELECT history_months FROM bank_connections"))["history_months"]);

        // A bank added after that gets the 12 months the person chose, not 3.
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        Assert.Equal(
            Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-12)),
            Assert.Single(factory.Pluggy.WindowsAsked(FakePluggyServer.OtherCheckingAccountId)).From);
    }

    [Fact]
    public async Task ConnectingAgain_AConnectionNeverSynchronised_TakesThePeriodSent()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 12);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);

        // Nothing was read yet: the period is still a choice.
        Assert.Equal(connectionId, await ConnectAsync(ana, historyMonths: 6));

        Assert.Equal(6L, Assert.Single(await factory.RowsAsync("SELECT history_months FROM bank_connections"))["history_months"]);
        await SyncAsync(factory, ana, connectionId);
        Assert.Equal(
            Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-6)),
            factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId)[0].From);
    }

    [Fact]
    public async Task ABankThatCouldNotBeReadForWeeks_WhileTheConnectionKeptSynchronising_IsAskedFromItsOwnLastTransaction_NoGap()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);
        var today = DateTime.UtcNow.Date;
        // What the other bank had when it was last read, 30 days ago.
        factory.Pluggy.Transactions[FakePluggyServer.OtherCheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000f1", today.AddDays(-30).AddHours(15), -12m) { Description = "Padaria Modelo" },
        ];
        await SyncAsync(factory, ana, connectionId);

        // Since then the other bank asks for a new login, and the connection goes on synchronising with the first bank.
        factory.Pluggy.ItemStatusOverride[FakePluggyServer.OtherItemWithAccounts] = "LOGIN_ERROR";
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        Assert.Single(factory.Pluggy.WindowsAsked(FakePluggyServer.OtherCheckingAccountId));

        // The person logs in again; meanwhile the bank had a purchase 20 days ago, older than "last synchronisation - 7".
        factory.Pluggy.ItemStatusOverride.Remove(FakePluggyServer.OtherItemWithAccounts);
        factory.Pluggy.Transactions[FakePluggyServer.OtherCheckingAccountId].Add(
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000f2", today.AddDays(-20).AddHours(15), -33m) { Description = "Mercado Modelo" });

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        // The account that was not being read: from its own last transaction minus 7 days.
        var lastDay = BrazilDay(today.AddDays(-30).AddHours(15));
        Assert.Equal(Text(lastDay.AddDays(-7)), factory.Pluggy.WindowsAsked(FakePluggyServer.OtherCheckingAccountId)[1].From);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000f2"))["review_state"]);
        // The account that was being read all along: 7 days before its last transaction (2 days ago), which is
        // earlier than 7 days before the last synchronisation (today) and so is the one asked.
        var checking = factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId);
        Assert.Equal(Text(BrazilDay(today.AddDays(-2).AddHours(15)).AddDays(-7)), checking[^1].From);
    }

    [Fact]
    public async Task AnAccountWhoseLastTransactionIsOlderThanTheHistory_IsNeverAskedFurtherBackThanTheHistory()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        var today = DateTime.UtcNow.Date;
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000f3", today.AddDays(-80).AddHours(15), -12m) { Description = "Padaria Modelo" },
        ];
        await SyncAsync(factory, ana, connectionId);
        // A month goes by: the only line of the account is now older than the 3 months.
        factory.Clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        var second = factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId)[1];
        Assert.Equal(Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-3)), second.From);
    }

    // ---------------------------------------------------------------- what vanished at the bank

    [Fact]
    public async Task ALineStillPendingAtTheBank_ThatPluggyNoLongerLists_LeavesTheMirror_AndTheReview()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await SyncAsync(factory, ana, connectionId);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, FakePluggyServer.CardPendingTransactionId))["status"]);
        Assert.Equal(4, await WaitingAsync());

        // The pre-authorisation fell at the bank: Pluggy stops listing it.
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId].RemoveAll(t => t.Id == FakePluggyServer.CardPendingTransactionId);
        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", run["status"]);
        var ids = (await MirrorAsync(factory)).Select(r => (string)r["pluggy_transaction_id"]!).ToList();
        Assert.DoesNotContain(FakePluggyServer.CardPendingTransactionId, ids);
        // Everything else is where it was: settled lines are never taken out, listed or not.
        Assert.Equal(4, ids.Count);
        Assert.Equal(3, await WaitingAsync());

        // Everything that waits in the review, month by month (also what cannot be confirmed yet).
        async Task<int> WaitingAsync()
            => (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review"))
                .GetProperty("pendingByMonth").EnumerateArray().Sum(m => m.GetProperty("pending").GetInt32());
    }

    [Fact]
    public async Task WhatVanishedAtTheBank_IsKept_WhenItWasSettled_WhenSomeoneReviewedIt_AndWhenTheReadingDidNotCoverItsDay()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        var today = DateTime.UtcNow.Date;
        const string discardedPending = "c1b2c3d4-0000-4000-8000-0000000000d1";
        const string oldPending = "c1b2c3d4-0000-4000-8000-0000000000d2";
        const string firstDayPending = "c1b2c3d4-0000-4000-8000-0000000000d3";
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId].AddRange(
        [
            new FakeTransaction(discardedPending, today.AddDays(-1).AddHours(13), 20m) { Description = "Posto Exemplo", Status = "PENDING" },
            new FakeTransaction(oldPending, today.AddDays(-40).AddHours(13), 21m) { Description = "Hotel Exemplo", Status = "PENDING" },
        ]);
        await SyncAsync(factory, ana, connectionId);
        // The person discarded one of the pending lines.
        var discardedId = Guid.Parse((string)(await MirrorRowAsync(factory, discardedPending))["id"]!);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { discardedId } })).StatusCode);
        // A pending line right on the first day the next reading asks for (the last transaction of the account is of
        // yesterday, so the reading starts 7 days before it; Pluggy cuts that day in UTC).
        var firstDayAsked = BrazilDay(today.AddDays(-1).AddHours(20)).AddDays(-7);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId].Add(
            new FakeTransaction(firstDayPending, firstDayAsked.ToDateTime(new TimeOnly(1, 0), DateTimeKind.Utc), 22m) { Description = "Bar Exemplo", Status = "PENDING" });
        await SyncAsync(factory, ana, connectionId);
        Assert.Equal(8, (await MirrorAsync(factory)).Count);

        // Pluggy stops listing everything of the card.
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId].Clear();
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        var left = (await MirrorAsync(factory)).ToDictionary(r => (string)r["pluggy_transaction_id"]!);
        Assert.Contains(FakePluggyServer.CardPurchaseTransactionId, left.Keys); // settled
        Assert.Equal("Discarded", left[discardedPending]["review_state"]); // someone reviewed it
        Assert.Contains(oldPending, left.Keys); // 40 days ago: the reading (last transaction - 7 days) did not go that far
        Assert.Contains(firstDayPending, left.Keys); // the first day asked is not taken as covered
        Assert.DoesNotContain(FakePluggyServer.CardPendingTransactionId, left.Keys); // the only one that goes
        Assert.Equal(7, left.Count);
    }

    [Fact]
    public async Task ARun_ReadsEveryPageOfAnAccount()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 1100)
            .Select(i => new FakeTransaction($"e1b2c3d4-0000-4000-8000-{i:D12}", day.AddSeconds(i), -i) { Description = $"Compra {i}" })
            .ToList();

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(1102L, run["transactions_new"]);
        Assert.Equal(1102L, Assert.Single(await factory.RowsAsync("SELECT count(*) AS n FROM bank_transactions"))["n"]);
        Assert.Equal(1102L, Assert.Single(await factory.RowsAsync("SELECT count(DISTINCT pluggy_transaction_id) AS n FROM bank_transactions"))["n"]);
    }

    [Fact]
    public async Task AnAccountWithTheSynchronisationTurnedOff_IsNotRead()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        var item = await AddItemAsync(ana, connectionId);
        var card = item.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("type").GetString() == "CREDIT").GetProperty("id").GetGuid();
        (await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{card}", new { syncEnabled = false })).EnsureSuccessStatusCode();

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(3L, run["transactions_new"]);
        Assert.Empty(factory.Pluggy.WindowsAsked(FakePluggyServer.CreditCardAccountId));
        Assert.Single(factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId));
        // The choice of the person stays after the accounts are refreshed.
        Assert.Equal(0L, Assert.Single(await factory.RowsAsync($"SELECT sync_enabled FROM bank_accounts WHERE pluggy_account_id = '{FakePluggyServer.CreditCardAccountId}'"))["sync_enabled"]);
    }

    [Fact]
    public async Task ADateWithoutTime_IsMirroredOnTheDayAsWritten_AndATimedOneOnItsDayInBrazil()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-6);
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000f1", day, -10m) { Description = "Sem hora" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000f2", day.AddHours(1).AddMinutes(30), -10m) { Description = "De madrugada em UTC" },
        ];

        await SyncAsync(factory, ana, connectionId);

        Assert.Equal(Day(day), (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000f1"))["local_date"]);
        // 01:30 UTC is 22:30 of the day before in Brasília.
        Assert.Equal(Day(day.AddDays(-1)), (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000f2"))["local_date"]);
    }

    // ---------------------------------------------------------------- items that need the person, and Pluggy failing

    [Fact]
    public async Task AnItemWithLoginError_DoesNotStopTheRunOfTheOthers()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);
        factory.Pluggy.ItemStatusOverride[FakePluggyServer.ItemWithAccounts] = "LOGIN_ERROR";

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(1L, run["transactions_new"]);
        Assert.Equal(FakePluggyServer.OtherAccountTransactionId, Assert.Single(await MirrorAsync(factory))["pluggy_transaction_id"]);
        // The item is marked as Pluggy says, and nothing of it was asked.
        Assert.Equal("LOGIN_ERROR", Assert.Single(await factory.RowsAsync($"SELECT status FROM bank_items WHERE pluggy_item_id = '{FakePluggyServer.ItemWithAccounts}'"))["status"]);
        Assert.Empty(factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId));
        var connection = Assert.Single(await factory.RowsAsync("SELECT status, last_sync_at_utc FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.NotNull(connection["last_sync_at_utc"]);
    }

    [Theory]
    [InlineData("LOGIN_ERROR")]
    [InlineData("WAITING_USER_INPUT")]
    public async Task WhenNoItemCanBeRead_TheRunFails_AndTheLastSynchronisationDoesNotMove(string itemStatus)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.ItemStatusOverride[FakePluggyServer.ItemWithAccounts] = itemStatus;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("PLUGGY_ITEM_NEEDS_ACTION", run["error_code"]);
        Assert.Contains("Meu Pluggy", (string)run["error_message"]!, StringComparison.Ordinal);
        Assert.Empty(await MirrorAsync(factory));
        Assert.Null(Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "PLUGGY_UNAVAILABLE")]
    [InlineData(HttpStatusCode.TooManyRequests, "PLUGGY_RATE_LIMITED")]
    public async Task WhenPluggyFails_TheRunFailsWithTheReason_TheLastSynchronisationDoesNotMove_AndTheConnectionStaysActive(HttpStatusCode status, string code)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await SyncAsync(factory, ana, connectionId);
        var before = Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"];
        factory.Pluggy.TransactionsStatus = status;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal(code, run["error_code"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)run["error_message"]));
        var connection = Assert.Single(await factory.RowsAsync("SELECT status, last_sync_at_utc, last_error_code FROM bank_connections"));
        Assert.Equal(before, connection["last_sync_at_utc"]);
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_error_code"]);

        // The next run, with Pluggy back, works.
        factory.Pluggy.TransactionsStatus = null;
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
    }

    [Fact]
    public async Task WhenPluggyRefusesTheStoredCredentials_TheRunFails_AndTheConnectionShowsTheError()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.ExpireIssuedKeys();
        factory.Pluggy.AuthStatus = HttpStatusCode.Unauthorized; // regenerated or revoked at Pluggy

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("PLUGGY_INVALID_CREDENTIALS", run["error_code"]);
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Error", connection["status"]);
        Assert.Equal("PLUGGY_INVALID_CREDENTIALS", connection["last_error_code"]);
        Assert.Contains("Desconecte e conecte de novo", (string)connection["last_error_message"]!, StringComparison.Ordinal);
        Assert.Null(connection["last_sync_at_utc"]);
        Assert.NotNull(connection["client_secret_encrypted"]);
    }

    // ---------------------------------------------------------------- the manual button

    [Fact]
    public async Task OnlyAForcedRun_AsksPluggyToReadTheBanksAgain_AndARefusalOfThatDoesNotFailTheRun()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        await SyncAsync(factory, ana, connectionId);
        Assert.Equal(0, factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"));

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId, force: true))["status"]);
        Assert.Equal(1, factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"));

        // Pluggy limits how often an item is read again: the run reads what Pluggy already has.
        factory.Pluggy.ItemUpdateStatus = HttpStatusCode.TooManyRequests;
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId, force: true))["status"]);
        Assert.Equal(2, factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NoRefusalOfTheNewReading_FailsTheRun_NorMarksTheConnection(HttpStatusCode refusal)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        // Pluggy does not let this item be read again on request (it may be so for every item of Meu Pluggy).
        factory.Pluggy.ItemUpdateStatus = refusal;

        var run = await SyncAsync(factory, ana, connectionId, force: true);

        Assert.Equal("Done", run["status"]);
        Assert.Null(run["error_code"]);
        Assert.Equal(5L, run["transactions_new"]);
        Assert.True(factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}") >= 1);
        var connection = Assert.Single(await factory.RowsAsync("SELECT status, last_error_code, last_error_message, last_sync_at_utc FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_error_code"]);
        Assert.Null(connection["last_error_message"]);
        Assert.NotNull(connection["last_sync_at_utc"]);
    }

    // ---------------------------------------------------------------- category: the table, then the AI only with consent

    [Fact]
    public async Task ACategoryTheTableDoesNotKnow_IsOutros_AndGoesToTheAiOnlyWithTheConsentOfWhoConnected()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        factory.Classifier.Answer = "Lazer";
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000a1", day.AddHours(13), -80m) { Description = "Parque Exemplo", Category = "Other", CategoryId = "99999999" },
        ];
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];

        // Without consent: nothing leaves for the AI.
        await SyncAsync(factory, ana, connectionId);
        Assert.Empty(factory.Classifier.Asked);
        Assert.Equal("OUTROS", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000a1"))["suggested_category"]);

        // With consent: only what the table does not know, only expenses with a description; the entry is not sent.
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId].AddRange(
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000a2", day.AddHours(14), -30m) { Description = "Clube Exemplo" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000a3", day.AddHours(15), -20m) { Description = "Cantina Exemplo", Category = "Eating out", CategoryId = "11010000" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000a4", day.AddHours(16), -5m),
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000a5", day.AddHours(17), 900m) { Type = "CREDIT", Description = "Entrada Exemplo" },
        ]);
        await SyncAsync(factory, ana, connectionId, aiConsent: true);

        Assert.Equal(new[] { "Clube Exemplo" }, factory.Classifier.Asked);
        Assert.Equal("LAZER", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000a2"))["suggested_category"]);
        Assert.Equal("ALIMENTACAO", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000a3"))["suggested_category"]);
        Assert.Equal("OUTROS", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000a4"))["suggested_category"]);
        Assert.Null((await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000a5"))["suggested_category"]);
        // The line that was already in the mirror keeps the suggestion it got when it arrived.
        Assert.Equal("OUTROS", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000a1"))["suggested_category"]);
    }

    [Fact]
    public async Task TheAi_IsAskedAtMost30TimesPerRun_AndAFailureOfItNeverFailsTheRun()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 40)
            .Select(i => new FakeTransaction($"c1b2c3d4-0000-4000-8000-{i:D12}", day.AddMinutes(i), -i) { Description = $"Compra {i}" })
            .ToList();
        factory.Classifier.Answer = "uma categoria que não existe";

        var run = await SyncAsync(factory, ana, connectionId, aiConsent: true);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(30, factory.Classifier.Asked.Count);
        // An answer that is not one of the seven keys is dropped.
        Assert.All(await MirrorAsync(factory), row => Assert.Equal("OUTROS", row["suggested_category"]));

        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId].Add(
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000b1", day.AddHours(5), -9m) { Description = "Compra nova" });
        factory.Classifier.Failure = new InvalidOperationException("AI down (fake)");

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId, aiConsent: true))["status"]);
        Assert.Equal("OUTROS", (await MirrorRowAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000b1"))["suggested_category"]);
    }

    [Fact]
    public async Task TheAi_HasFifteenSecondsPerRun_WhatIsLeftAfterThatStaysWithTheTable()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 5)
            .Select(i => new FakeTransaction($"c1b2c3d4-0000-4000-8000-{i:D12}", day.AddMinutes(i), -i) { Description = $"Compra {i}" })
            .ToList();
        factory.Classifier.Answer = "Lazer";
        // The AI is slow: each answer takes 6 seconds (of the clock of the API).
        factory.Classifier.OnAsked = () => factory.Clock.Advance(TimeSpan.FromSeconds(6));

        var run = await SyncAsync(factory, ana, connectionId, aiConsent: true);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(5L, run["transactions_new"]);
        // Asked at 0 s, 6 s and 12 s; at 18 s the 15 seconds are over and nothing else is sent.
        Assert.Equal(new[] { "Compra 1", "Compra 2", "Compra 3" }, factory.Classifier.Asked);
        var suggested = (await MirrorAsync(factory)).Select(r => (string)r["suggested_category"]!).ToList();
        Assert.Equal(new[] { "LAZER", "LAZER", "LAZER", "OUTROS", "OUTROS" }, suggested);
    }

    [Fact]
    public async Task TheFifteenSecondsOfTheAi_CountOnlyTheTimeSpentWithTheAi_NotTheTimePluggyTakes()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 4)
            .Select(i => new FakeTransaction($"c1b2c3d4-0000-4000-8000-{i:D12}", day.AddMinutes(i), -i) { Description = $"Compra {i}" })
            .ToList();
        factory.Classifier.Answer = "Lazer";
        // Pluggy is slow today: each list of transactions takes 40 seconds (of the clock of the API) to arrive.
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path == "/transactions") factory.Clock.Advance(TimeSpan.FromSeconds(40));
            return Task.CompletedTask;
        };
        // The AI itself answers in 6 seconds.
        factory.Classifier.OnAsked = () => factory.Clock.Advance(TimeSpan.FromSeconds(6));

        var run = await SyncAsync(factory, ana, connectionId, aiConsent: true);

        Assert.Equal("Done", run["status"]);
        // The wait for Pluggy took nothing of the 15 seconds: asked at 0 s, 6 s and 12 s of AI time; the fourth is over the limit.
        Assert.Equal(new[] { "Compra 1", "Compra 2", "Compra 3" }, factory.Classifier.Asked);
        var suggested = (await MirrorAsync(factory)).Select(r => (string)r["suggested_category"]!).ToList();
        Assert.Equal(new[] { "LAZER", "LAZER", "LAZER", "OUTROS" }, suggested);
    }

    // ---------------------------------------------------------------- a review at the same moment as a run

    [Fact]
    public async Task ALineConfirmedWhileARunWasRefreshingIt_KeepsTheReviewAndTheLink_TakesWhatPluggySaysNow_AndTheRunEndsWell()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var restaurant = Guid.Parse((string)(await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId))["id"]!);
        var listed = factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId];
        var index = listed.FindIndex(t => t.Id == FakePluggyServer.RestaurantTransactionId);
        listed[index] = listed[index] with { Description = "Cantina Exemplo Centro" };
        // The run has read the lines of the checking account and is about to save them when someone confirms one.
        var once = 0;
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path != "/transactions"
                || !request.Query.Contains(FakePluggyServer.CheckingAccountId, StringComparison.Ordinal)
                || Interlocked.Exchange(ref once, 1) == 1)
            {
                return Task.CompletedTask;
            }

            factory.BeforeNextSave = async () =>
            {
                var confirmed = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { expenses = new[] { new { id = restaurant } } });
                Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
            };
            return Task.CompletedTask;
        };

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.True("Done" == (string)run["status"]!, $"{run["error_code"]}: {run["error_message"]}");
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Confirmed", line["review_state"]);
        var transaction = Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Equal(transaction["id"], line["linked_transaction_id"]);
        Assert.NotNull(line["reviewed_at_utc"]);
        Assert.Equal("Cantina Exemplo Centro", line["description"]);
        Assert.Equal(run["id"], line["sync_run_id"]);
        Assert.NotNull(Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]);
    }

    [Fact]
    public async Task ALineThatVanishedAtTheBank_DiscardedWhileTheRunWasRemovingIt_StaysAsThePersonLeftIt()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var pending = Guid.Parse((string)(await MirrorRowAsync(factory, FakePluggyServer.CardPendingTransactionId))["id"]!);
        // The pre-authorisation falls at the bank; the run sees that and is about to take the line out of the mirror
        // when someone discards it in the review.
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId].RemoveAll(t => t.Id == FakePluggyServer.CardPendingTransactionId);
        var once = 0;
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path != "/transactions"
                || !request.Query.Contains(FakePluggyServer.CreditCardAccountId, StringComparison.Ordinal)
                || Interlocked.Exchange(ref once, 1) == 1)
            {
                return Task.CompletedTask;
            }

            factory.BeforeNextSave = async () =>
            {
                var discarded = await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { pending } });
                Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
            };
            return Task.CompletedTask;
        };

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.True("Done" == (string)run["status"]!, $"{run["error_code"]}: {run["error_message"]}");
        // Someone reviewed it: never touched by a run again.
        Assert.Equal("Discarded", (await MirrorRowAsync(factory, FakePluggyServer.CardPendingTransactionId))["review_state"]);
        Assert.Equal(5, (await MirrorAsync(factory)).Count);
    }

    // ---------------------------------------------------------------- a run that fails for a reason nobody foresaw

    [Fact]
    public async Task WhenTheStoreRefusesASaveInTheMiddleOfARun_TheRunIsSyncFailed_TheConnectionIsLeftAsItWas_OnlyTheKindIsLogged_AndTheNextRunEndsWell()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var before = Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"];
        Assert.NotNull(before);
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId].Add(
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000f1", DateTime.UtcNow.Date.AddDays(-1).AddHours(13), -31.00m) { Description = "Padaria Exemplo" });
        // The store refuses the new line of this run, for a reason that is none of the foreseen ones (not a
        // concurrency token, not a key): the run has already saved the item and the accounts by then.
        await factory.ExecuteAsync(
            "CREATE TRIGGER refuse_mirror BEFORE INSERT ON bank_transactions BEGIN SELECT RAISE(ABORT, 'falha simulada do banco de dados'); END");

        var failed = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", failed["status"]);
        Assert.Equal("SYNC_FAILED", failed["error_code"]);
        Assert.Equal("Não foi possível sincronizar agora. Tente de novo em alguns minutos.", failed["error_message"]);
        Assert.NotNull(failed["finished_at_utc"]);
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Equal(before, connection["last_sync_at_utc"]);
        Assert.Null(connection["last_error_code"]);
        Assert.Null(connection["last_error_message"]);
        Assert.NotNull(connection["client_secret_encrypted"]);
        Assert.Equal(5, (await MirrorAsync(factory)).Count);
        // The log of the run says which kind of failure it was, and nothing of what was being written.
        var logged = Assert.Single(factory.Logs.Lines, l => l.StartsWith("Error CoupleSync.Application.OpenFinance.SyncConnectionService:", StringComparison.Ordinal));
        Assert.Matches(@"^Error CoupleSync\.Application\.OpenFinance\.SyncConnectionService: Open Finance run [0-9a-f-]{36} failed with DataStoreException\. $", logged);
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("Padaria Exemplo", StringComparison.Ordinal));

        await factory.ExecuteAsync("DROP TRIGGER refuse_mirror");
        var next = await SyncAsync(factory, ana, connectionId);

        Assert.True("Done" == (string)next["status"]!, $"{next["error_code"]}: {next["error_message"]}");
        Assert.Equal(6, (await MirrorAsync(factory)).Count);
        var after = (string)Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]!;
        Assert.True(string.CompareOrdinal(after, (string)before!) > 0, $"{after} should be after {before}");
    }

    [Fact]
    public async Task WhenTheLinesOfARunAreReviewedAgainAtEachOfItsFourAttemptsToSave_TheRunIsSyncFailed_AndTheMirrorAndTheReviewsStayAsPeopleLeftThem()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var first = await SyncAsync(factory, ana, connectionId);
        Assert.Equal("Done", first["status"]);
        var before = Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"];
        var restaurant = Guid.Parse((string)(await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId))["id"]!);
        var ride = Guid.Parse((string)(await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId))["id"]!);
        var descriptionBefore = (await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId))["description"];
        var listed = factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId];
        var index = listed.FindIndex(t => t.Id == FakePluggyServer.RestaurantTransactionId);
        listed[index] = listed[index] with { Description = "Cantina Exemplo Centro" };

        // Every time the run is about to save the lines of the checking account, someone has just reviewed one of them.
        var attempts = 0;
        Func<Task> review = null!;
        review = async () =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            var answer = attempt switch
            {
                1 => await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { restaurant } }),
                2 => await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { ride } }),
                3 => await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { ride }),
                _ => await ana.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { ride } }),
            };
            Assert.True(HttpStatusCode.OK == answer.StatusCode, await answer.Content.ReadAsStringAsync());
            // The fifth save, if the run tried one, would find nobody in its way and go through.
            if (attempt < 4) factory.BeforeNextSave = review;
        };
        var once = 0;
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path == "/transactions"
                && request.Query.Contains(FakePluggyServer.CheckingAccountId, StringComparison.Ordinal)
                && Interlocked.Exchange(ref once, 1) == 0)
            {
                factory.BeforeNextSave = review;
            }

            return Task.CompletedTask;
        };

        var failed = await SyncAsync(factory, ana, connectionId);

        Assert.Equal(4, attempts);
        Assert.Equal("Failed", failed["status"]);
        Assert.Equal("SYNC_FAILED", failed["error_code"]);
        Assert.Equal("Não foi possível sincronizar agora. Tente de novo em alguns minutos.", failed["error_message"]);
        // The reviews are as people left them, and nothing the run read was written over the mirror.
        var restaurantLine = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Discarded", restaurantLine["review_state"]);
        Assert.NotNull(restaurantLine["reviewed_at_utc"]);
        Assert.Equal(descriptionBefore, restaurantLine["description"]);
        Assert.Equal(first["id"], restaurantLine["sync_run_id"]);
        Assert.Equal("Discarded", (await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId))["review_state"]);
        Assert.Equal(5, (await MirrorAsync(factory)).Count);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Equal(before, connection["last_sync_at_utc"]);
        Assert.Null(connection["last_error_code"]);

        // The next run, with nobody reviewing at the same moment, reads them again and keeps the reviews.
        var next = await SyncAsync(factory, ana, connectionId);

        Assert.True("Done" == (string)next["status"]!, $"{next["error_code"]}: {next["error_message"]}");
        restaurantLine = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Cantina Exemplo Centro", restaurantLine["description"]);
        Assert.Equal("Discarded", restaurantLine["review_state"]);
    }

    // ---------------------------------------------------------------- branches the final reading found without a test

    [Fact]
    public async Task ARunOfAConnectionWithoutAnyBank_FailsAsNoBank_WithoutCallingPluggy()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana); // credentials stored, no bank verified yet
        var calls = factory.Pluggy.Requests.Count;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("SYNC_NO_BANK", run["error_code"]);
        Assert.Equal("Nenhum banco verificado nesta conexão. Adicione um banco e sincronize de novo.", run["error_message"]);
        Assert.Equal(calls, factory.Pluggy.Requests.Count);
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_sync_at_utc"]);
        Assert.Null(connection["last_error_code"]);
    }

    [Fact]
    public async Task ARunThatSomeoneElseEndedWhileItWasBeingTaken_IsLeftAsItIs_AndTheJobGoesOn()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var calls = factory.Pluggy.Requests.Count;
        // The next save of the API is the job taking the run (Pending → Running): right before it, another process
        // gives the run its verdict.
        factory.BeforeNextSave = () => factory.ExecuteAsync(
            "UPDATE sync_runs SET status = 'Failed', error_code = 'SYNC_INTERRUPTED', error_message = 'Encerrada por outro processo.'");

        var taken = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("SYNC_INTERRUPTED", taken["error_code"]);
        await Task.Delay(300); // several passes of the job
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM sync_runs"));
        Assert.Equal("Failed", row["status"]);
        Assert.Equal("Encerrada por outro processo.", row["error_message"]);
        Assert.Null(row["started_at_utc"]);
        Assert.Equal(calls, factory.Pluggy.Requests.Count);
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("Unhandled error in the OpenFinanceSyncJob", StringComparison.Ordinal));

        var next = await SyncAsync(factory, ana, connectionId);
        Assert.True("Done" == (string)next["status"]!, $"{next["error_code"]}: {next["error_message"]}");
    }

    [Fact]
    public async Task ARunRunningForTooLong_ThatGoesAwayWhileThePassWasFailingIt_DoesNotStopTheJob()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno");
        var connectionId = await ConnectWithBankAsync(ana);
        var other = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, other, FakePluggyServer.OtherItemWithAccounts);
        await EnqueueAsync(factory, ana, connectionId, status: "Running");
        // The next save of the API is the pass failing that run: right before it the run is deleted (the person left).
        factory.BeforeNextSave = () => factory.ExecuteAsync("DELETE FROM sync_runs");

        factory.Clock.Advance(TimeSpan.FromMinutes(10.5));
        await WaitUntilAsync(async () => (await factory.RowsAsync("SELECT id FROM sync_runs")).Count == 0);

        // The same pass, and the ones after it, still look at the queue.
        var next = await SyncAsync(factory, bruno, other);
        Assert.True("Done" == (string)next["status"]!, $"{next["error_code"]}: {next["error_message"]}");
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("Could not fail Open Finance runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenPluggyRefusesTheCredentials_AndThePersonDisconnectsAtThatVeryMoment_TheRunFails_AndTheConnectionStaysDisconnected_NotInError()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.ExpireIssuedKeys();
        factory.Pluggy.AuthStatus = HttpStatusCode.Unauthorized;
        // The run is about to write "error" on the connection when the person disconnects it.
        var once = 0;
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path == "/auth" && Interlocked.Exchange(ref once, 1) == 0)
            {
                factory.BeforeNextSave = async () =>
                    Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
            }

            return Task.CompletedTask;
        };

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("PLUGGY_INVALID_CREDENTIALS", run["error_code"]);
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Disconnected", connection["status"]);
        Assert.Null(connection["last_error_code"]);
        Assert.Null(connection["last_error_message"]);
        Assert.Null(connection["client_secret_encrypted"]);
    }

    [Fact]
    public async Task TheScheduler_WhenSomeoneAsksForARunOfAConnectionAtThatVeryMoment_EnqueuesNoSecondOne_AndGoesOnToTheOtherConnections()
    {
        await using var factory = new OpenFinanceApiFactory(); // slow job, scheduler off: the pass is called here
        var today = BrazilDay(DateTime.UtcNow);
        factory.Clock.SetNow(BrazilTime.ToUtc(today.ToDateTime(new TimeOnly(6, 30))));
        var ana = await factory.RegisterAsync("Ana");
        var first = await ConnectWithBankAsync(ana);
        var bruno = await factory.RegisterAsync("Bruno");
        var second = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, second, FakePluggyServer.OtherItemWithAccounts);
        await factory.ExecuteAsync("UPDATE bank_connections SET last_sync_at_utc = @at", ("@at", factory.Clock.UtcNow.AddHours(-30)));
        // The scheduler is about to store the run of Ana's connection (the older one) when she asks for one herself.
        factory.BeforeNextSave = async () => await EnqueueAsync(factory, ana, first);

        int enqueued;
        using (var scope = factory.Services.CreateScope())
        {
            enqueued = await scope.ServiceProvider.GetRequiredService<CoupleSync.Application.OpenFinance.SyncRunService>()
                .EnqueueDailyRunsAsync(CancellationToken.None);
        }

        Assert.Equal(1, enqueued);
        var runs = await factory.RowsAsync("SELECT connection_id, triggered_by FROM sync_runs ORDER BY triggered_by");
        Assert.Equal(
            new[] { (second.ToString().ToUpperInvariant(), "Scheduler"), (first.ToString().ToUpperInvariant(), "User") },
            runs.Select(r => ((string)r["connection_id"]!, (string)r["triggered_by"]!)));
    }

    // ---------------------------------------------------------------- the run never writes a connection that changed

    [Fact]
    public async Task DisconnectedInTheMiddleOfARun_TheRunStops_AndTheConnectionStaysDisconnected()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        // While Pluggy is answering the first list of transactions, the person disconnects.
        var once = 0;
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (request.Path != "/transactions" || Interlocked.Exchange(ref once, 1) == 1) return;
            var disconnected = await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}");
            Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        };

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("BANK_CONNECTION_CHANGED", run["error_code"]);
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Disconnected", connection["status"]);
        Assert.Null(connection["client_secret_encrypted"]);
        Assert.Null(connection["client_id_encrypted"]);
        Assert.Null(connection["last_sync_at_utc"]);
        Assert.Null(connection["last_error_code"]);
        // Nothing read after the disconnection was stored, and Pluggy was not asked for anything else.
        Assert.Empty(await MirrorAsync(factory));
        Assert.Equal(1, factory.Pluggy.Count("GET", "/transactions"));
    }

    [Fact]
    public async Task ConnectedAgainInTheMiddleOfARun_TheRunStops_AndNeitherMarksTheNewCredentialsNorKeepsItsKey()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var once = 0;
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (request.Path != "/transactions" || Interlocked.Exchange(ref once, 1) == 1) return;
            (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).EnsureSuccessStatusCode();
            await ConnectAsync(ana); // the same connection, with credentials stored again
        };

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("BANK_CONNECTION_CHANGED", run["error_code"]);
        var connection = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_sync_at_utc"]);
        Assert.Empty(await MirrorAsync(factory));

        // The next run authenticates with the credentials stored now: the key of the old ones is not reused.
        var authBefore = factory.Pluggy.AuthCalls;
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        Assert.Equal(authBefore + 1, factory.Pluggy.AuthCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhoConnectedLeavesInTheMiddleOfARun_NothingOfTheirsStays(bool removedByTheOwner)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectWithBankAsync(bruno);
        var once = 0;
        factory.Pluggy.BeforeAnswer = async request =>
        {
            // After the first account was written: the mirror already has rows when the person leaves.
            if (request.Path != "/transactions" || !request.Query.Contains(FakePluggyServer.CreditCardAccountId, StringComparison.Ordinal)
                || Interlocked.Exchange(ref once, 1) == 1)
            {
                return;
            }

            var gone = removedByTheOwner
                ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
                : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
            Assert.True(gone.IsSuccessStatusCode, await gone.Content.ReadAsStringAsync());
        };

        var runId = await EnqueueAsync(factory, bruno, connectionId);

        // The run went away with the connection; the job goes on working (a later run of someone else is executed).
        await WaitUntilAsync(async () => (await factory.RowsAsync("SELECT id FROM bank_connections")).Count == 0);
        var anaConnection = await ConnectAsync(ana);
        await AddItemAsync(ana, anaConnection, FakePluggyServer.OtherItemWithAccounts);
        Assert.Equal("Done", (await SyncAsync(factory, ana, anaConnection))["status"]);

        Assert.Empty(await factory.RowsAsync($"SELECT id FROM sync_runs WHERE id = '{runId.ToString().ToUpperInvariant()}'"));
        Assert.Empty(await factory.RowsAsync($"SELECT id FROM bank_transactions WHERE user_id = '{bruno.UserId.ToString().ToUpperInvariant()}'"));
        Assert.Empty(await factory.RowsAsync($"SELECT id FROM bank_items WHERE pluggy_item_id = '{FakePluggyServer.ItemWithAccounts}'"));
        Assert.Single(await MirrorAsync(factory)); // only what Ana synchronised afterwards
    }

    [Fact]
    public async Task ARunOfAConnectionAlreadyDisconnected_FailsWithoutCallingPluggy_AndWritesNothingToIt()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).EnsureSuccessStatusCode();
        var before = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        var calls = factory.Pluggy.Requests.Count;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("BANK_CONNECTION_DISCONNECTED", run["error_code"]);
        Assert.Equal(calls, factory.Pluggy.Requests.Count);
        var after = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal(before["updated_at_utc"], after["updated_at_utc"]);
        Assert.Equal("Disconnected", after["status"]);
    }

    // ---------------------------------------------------------------- a connection of someone who is no longer in the group

    [Fact]
    public async Task AConnectionWhoseOwnerIsNoLongerInTheGroup_IsNotSynchronised_AndPluggyIsNotCalled()
    {
        // Data from before issue #31: someone connected and then left, while leaving still kept the connection.
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectWithBankAsync(bruno);
        await factory.ExecuteAsync("DELETE FROM couple_members WHERE user_id = @user", ("@user", bruno.UserId));
        var calls = factory.Pluggy.Requests.Count;

        var run = await SyncAsync(factory, bruno, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("SYNC_OWNER_NOT_IN_GROUP", run["error_code"]);
        Assert.Equal("Quem conectou estes bancos não faz mais parte do grupo. Nada foi sincronizado.", run["error_message"]);
        Assert.Equal(calls, factory.Pluggy.Requests.Count);
        Assert.Empty(await MirrorAsync(factory));
        Assert.Null(Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]);
    }

    // ---------------------------------------------------------------- the server without the key, or with another one

    [Fact]
    public async Task OnAServerWithoutTheKey_TheApiStarts_AndARunFailsAsUnavailable_WithoutCallingPluggy()
    {
        await using var factory = new OpenFinanceApiFactory(encryptionKey: null) { FastSync = true };
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await SeedConnectionAsync(factory, ana, "not-readable", "not-readable-either");

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("OPENFINANCE_UNAVAILABLE", run["error_code"]);
        Assert.Equal(
            "O Open Finance não está disponível neste servidor. Ele depende de uma configuração que ainda não foi feita.",
            run["error_message"]);
        Assert.Empty(factory.Pluggy.Requests);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task CredentialsStoredWithAnotherKey_MakeTheRunFailAsUnreadable_WithoutCallingPluggy()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await factory.ExecuteAsync(
            "UPDATE bank_connections SET client_id_encrypted = 'v1.AAAA.BBBB.CCCC', client_secret_encrypted = 'v1.DDDD.EEEE.FFFF'");
        var calls = factory.Pluggy.Requests.Count;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("OPENFINANCE_CREDENTIALS_UNREADABLE", run["error_code"]);
        Assert.Contains("Desconecte e conecte de novo", (string)run["error_message"]!, StringComparison.Ordinal);
        Assert.Equal(calls, factory.Pluggy.Requests.Count);
    }

    // ---------------------------------------------------------------- the queue

    [Fact]
    public async Task ARunLeftRunningByAProcessThatDied_IsFailedWhenTheApiStarts_WithAClearMessage()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var stuck = await EnqueueAsync(factory, ana, connectionId, status: "Running");
        await Task.Delay(300); // the job that is already up leaves it alone: it only takes runs that are waiting
        Assert.Equal("Running", Assert.Single(await factory.RowsAsync("SELECT status FROM sync_runs"))["status"]);

        // The API starts again on the same database.
        await using var restarted = factory.WithTestHostBuilder(_ => { });
        using var client = restarted.CreateClient();

        var run = await WaitForRunAsync(factory, stuck);
        Assert.Equal("Failed", run["status"]);
        Assert.Equal("SYNC_INTERRUPTED", run["error_code"]);
        Assert.Equal("A sincronização foi interrompida porque o servidor reiniciou. Sincronize de novo.", run["error_message"]);
        Assert.NotNull(run["finished_at_utc"]);
        Assert.Empty(await MirrorAsync(factory));
        // And a new run can be enqueued and is executed.
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
    }

    [Fact]
    public async Task ARunRunningForMoreThan10Minutes_IsFailedByThePassOfTheJob_WithoutAnyRestart_AndTheConnectionIsFreeAgain()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        // Left running by a process that is gone AFTER this one started (a deploy): the start of this one never saw it.
        var stuck = await EnqueueAsync(factory, ana, connectionId, status: "Running");
        await factory.ExecuteAsync("UPDATE sync_runs SET started_at_utc = @at WHERE id = @id", ("@at", factory.Clock.UtcNow), ("@id", stuck));

        // Young: it may be running in another process. It is left alone, and it holds the connection.
        factory.Clock.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(300);
        Assert.Equal("Running", Assert.Single(await factory.RowsAsync("SELECT status FROM sync_runs"))["status"]);
        await AssertErrorAsync(
            await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null), HttpStatusCode.Conflict, "SYNC_ALREADY_RUNNING");

        factory.Clock.Advance(TimeSpan.FromMinutes(1.5));

        var run = await WaitForRunAsync(factory, stuck);
        Assert.Equal("Failed", run["status"]);
        Assert.Equal("SYNC_TIMED_OUT", run["error_code"]);
        Assert.Equal("A sincronização demorou demais e foi encerrada. Sincronize de novo.", run["error_message"]);
        Assert.NotNull(run["finished_at_utc"]);
        // Nothing holds the connection any more: the person asks again and it runs.
        var again = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null);
        Assert.True(HttpStatusCode.Accepted == again.StatusCode, await again.Content.ReadAsStringAsync());
        Assert.Equal("Done", (await WaitForRunAsync(factory, (await JsonAsync(again)).GetProperty("id").GetGuid()))["status"]);
    }

    [Fact]
    public async Task ARunWithoutAStartRecorded_CountsFromWhenItWasCreated()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var stuck = await EnqueueAsync(factory, ana, connectionId, status: "Running");

        factory.Clock.Advance(TimeSpan.FromMinutes(10.5));

        Assert.Equal("SYNC_TIMED_OUT", (await WaitForRunAsync(factory, stuck))["error_code"]);
    }

    [Fact]
    public async Task WhenTheServerIsToldToStop_InTheMiddleOfARun_TheRunIsFailedAsInterrupted_RightThen()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (request.Path != "/transactions") return;
            reading.TrySetResult();
            await release.Task;
        };
        var runId = await EnqueueAsync(factory, ana, connectionId);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Running", Assert.Single(await factory.RowsAsync("SELECT status FROM sync_runs"))["status"]);

        // The host stops this job (what a deploy does to the old process), while Pluggy is still answering.
        var job = factory.Services.GetServices<IHostedService>().OfType<OpenFinanceSyncJob>().Single();
        var stopping = job.StopAsync(CancellationToken.None);
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        var run = Assert.Single(await factory.RowsAsync($"SELECT * FROM sync_runs WHERE id = '{runId.ToString().ToUpperInvariant()}'"));
        Assert.Equal("Failed", run["status"]);
        Assert.Equal("SYNC_INTERRUPTED", run["error_code"]);
        Assert.Equal("A sincronização foi interrompida porque o servidor reiniciou. Sincronize de novo.", run["error_message"]);
        var connection = Assert.Single(await factory.RowsAsync("SELECT status, last_sync_at_utc FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_sync_at_utc"]);
    }

    [Fact]
    public async Task TheDatabase_KeepsOneRunWaitingOrRunningPerConnection()
    {
        await using var factory = new OpenFinanceApiFactory(); // slow job: the first run is still waiting
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        await EnqueueAsync(factory, ana, connectionId);

        var second = await Assert.ThrowsAnyAsync<Exception>(() => EnqueueAsync(factory, ana, connectionId));

        Assert.Contains("UNIQUE", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await factory.RowsAsync("SELECT id FROM sync_runs"));
    }

    [Fact]
    public async Task NothingOfATransaction_ReachesTheLog_AtAnyLevel()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        var lines = factory.Logs.Lines;
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Contains(FakePluggyServer.RawOnlyMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Cantina Exemplo", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(FakePluggyServer.MerchantCnpj, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(FakePluggyServer.ClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("fake-api-key-", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the daily scheduler

    [Fact]
    public async Task TheScheduler_From6InBrasilia_EnqueuesOneRunPerConnectionWithoutASuccessIn20Hours_AndOnlyOnce()
    {
        await using var factory = new OpenFinanceApiFactory { FastSync = true, SchedulerOn = true };
        var today = BrazilDay(DateTime.UtcNow);
        // 05:50 in Brasília: nothing yet.
        factory.Clock.SetNow(BrazilTime.ToUtc(today.ToDateTime(new TimeOnly(5, 50))));
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var bruno = await factory.RegisterAsync("Bruno");
        var recent = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, recent, FakePluggyServer.OtherItemWithAccounts);
        await factory.ExecuteAsync("UPDATE bank_connections SET last_sync_at_utc = @at WHERE id = @id", ("@at", factory.Clock.UtcNow.AddHours(-30)), ("@id", connectionId));
        await factory.ExecuteAsync("UPDATE bank_connections SET last_sync_at_utc = @at WHERE id = @id", ("@at", factory.Clock.UtcNow.AddHours(-5)), ("@id", recent));
        await Task.Delay(500);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM sync_runs"));

        // 06:01: the connection without a success in 20 hours gets its run, and the job executes it.
        factory.Clock.SetNow(BrazilTime.ToUtc(today.ToDateTime(new TimeOnly(6, 1))));
        await WaitUntilAsync(async () => (await factory.RowsAsync("SELECT id FROM sync_runs WHERE status = 'Done'")).Count == 1);
        await Task.Delay(500); // many more ticks of the scheduler

        var run = Assert.Single(await factory.RowsAsync("SELECT * FROM sync_runs"));
        Assert.Equal("Scheduler", run["triggered_by"]);
        Assert.Equal(connectionId.ToString().ToUpperInvariant(), run["connection_id"]);
        Assert.Equal(0L, run["force_item_update"]);
        Assert.Equal(0L, run["ai_categorization_consent"]);
        Assert.Equal("Done", run["status"]);
        Assert.Equal(0, factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"));
    }

    [Fact]
    public async Task TheScheduler_LeavesAlone_DisconnectedConnections_ConnectionsOfWhoLeft_ConnectionsNeverSynchronised_AndConnectionsWithARecentRun()
    {
        await using var factory = new OpenFinanceApiFactory { FastSync = true, SchedulerOn = true };
        var today = BrazilDay(DateTime.UtcNow);
        factory.Clock.SetNow(BrazilTime.ToUtc(today.ToDateTime(new TimeOnly(5, 0))));
        var ana = await factory.RegisterAsync("Ana");
        var disconnected = await ConnectWithBankAsync(ana);
        (await ana.Client.DeleteAsync($"{Base}/connections/{disconnected}")).EnsureSuccessStatusCode();
        var bruno = await factory.RegisterAsync("Bruno");
        var orphan = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, orphan, FakePluggyServer.OtherItemWithAccounts);
        await factory.ExecuteAsync("DELETE FROM couple_members WHERE user_id = @user", ("@user", bruno.UserId));
        var carla = await factory.RegisterAsync("Carla");
        var failedToday = await SeedConnectionAsync(factory, carla, "x", "y");
        var failed = await EnqueueAsync(factory, carla, failedToday);
        await WaitForRunAsync(factory, failed); // failed a moment ago (unreadable credentials): not again today
        // Who connected and never synchronised has not chosen the period yet (nor asked for anything): the first
        // synchronisation is always the person's own.
        var dana = await factory.RegisterAsync("Dana");
        await ConnectAsync(dana, "Bancos da Dana");
        // Every other connection had its last success long ago: only the reasons above keep them out.
        await factory.ExecuteAsync(
            "UPDATE bank_connections SET last_sync_at_utc = @at WHERE user_id <> @dana",
            ("@at", factory.Clock.UtcNow.AddDays(-3)), ("@dana", dana.UserId));

        factory.Clock.SetNow(BrazilTime.ToUtc(today.ToDateTime(new TimeOnly(9, 0))));
        await Task.Delay(700);

        var runs = await factory.RowsAsync("SELECT * FROM sync_runs");
        Assert.Equal(failed.ToString().ToUpperInvariant(), Assert.Single(runs)["id"]);
    }

    // ---------------------------------------------------------------- leaving the group takes the mirror too

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingOrBeingRemoved_DeletesTheMirrorAndTheRunsOfThePerson_AndKeepsWhatWasConfirmed(bool removedByTheOwner)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var brunoConnection = await ConnectWithBankAsync(bruno);
        await SyncAsync(factory, bruno, brunoConnection);
        var anaConnection = await ConnectAsync(ana);
        await AddItemAsync(ana, anaConnection, FakePluggyServer.OtherItemWithAccounts);
        await SyncAsync(factory, ana, anaConnection);
        // A line of Bruno already became a transaction of the group.
        var transactionId = await SeedTransactionAsync(factory, bruno);
        await factory.ExecuteAsync(
            "UPDATE bank_transactions SET review_state = 'Confirmed', linked_transaction_id = @tx WHERE pluggy_transaction_id = @id",
            ("@tx", transactionId), ("@id", FakePluggyServer.RestaurantTransactionId));

        var gone = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(gone.IsSuccessStatusCode, await gone.Content.ReadAsStringAsync());

        // Only what Ana connected is left: her connection, her run, her line of the mirror.
        Assert.Equal(anaConnection.ToString().ToUpperInvariant(), Assert.Single(await factory.RowsAsync("SELECT id FROM bank_connections"))["id"]);
        Assert.Equal(anaConnection.ToString().ToUpperInvariant(), Assert.Single(await factory.RowsAsync("SELECT connection_id FROM sync_runs"))["connection_id"]);
        Assert.Equal(FakePluggyServer.OtherAccountTransactionId, Assert.Single(await MirrorAsync(factory))["pluggy_transaction_id"]);
        // The transaction already confirmed stays in the group.
        Assert.Equal(transactionId.ToString().ToUpperInvariant(), Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"))["id"]);
    }

    // ---------------------------------------------------------------- support

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail($"The condition was not met in {seconds} s.");
    }

    /// <summary>A transaction of the app (and its ingest row), as the review stores one, to be the target of a link.</summary>
    internal static async Task<Guid> SeedTransactionAsync(OpenFinanceApiFactory factory, Member member)
    {
        var ingestId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await factory.ExecuteAsync(
            """
            INSERT INTO transaction_event_ingests (id, couple_id, user_id, bank, amount, currency, event_timestamp_utc, status, created_at_utc)
            VALUES (@id, @couple, @user, 'OPENFINANCE', '45.0', 'BRL', @now, 'Accepted', @now)
            """,
            ("@id", ingestId), ("@couple", member.CoupleId), ("@user", member.UserId), ("@now", now));
        await factory.ExecuteAsync(
            """
            INSERT INTO transactions (id, couple_id, user_id, fingerprint, bank, amount, currency, event_timestamp_utc, category, ingest_event_id, source, created_at_utc)
            VALUES (@id, @couple, @user, @fingerprint, 'Banco Exemplo', '45.0', 'BRL', @now, 'SAUDE', @ingest, 3, @now)
            """,
            ("@id", id), ("@couple", member.CoupleId), ("@user", member.UserId), ("@fingerprint", Guid.NewGuid().ToString("N")),
            ("@ingest", ingestId), ("@now", now));
        return id;
    }

    /// <summary>A connection with an item, written straight to the tables (for servers that cannot connect: no key).</summary>
    private static async Task<Guid> SeedConnectionAsync(OpenFinanceApiFactory factory, Member member, string encryptedId, string encryptedSecret)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await factory.ExecuteAsync(
            """
            INSERT INTO bank_connections (id, couple_id, user_id, provider, label, client_id_encrypted, client_secret_encrypted, client_id_hint,
                                          status, history_months, created_at_utc, updated_at_utc)
            VALUES (@id, @couple, @user, 'PLUGGY', 'Bancos', @cid, @secret, '0a1b', 'Active', 3, @now, @now)
            """,
            ("@id", id), ("@couple", member.CoupleId), ("@user", member.UserId), ("@cid", encryptedId), ("@secret", encryptedSecret), ("@now", now));
        await factory.ExecuteAsync(
            """
            INSERT INTO bank_items (id, couple_id, connection_id, pluggy_item_id, connector_name, status, created_at_utc)
            VALUES (@id, @couple, @connection, @item, 'Banco Exemplo', 'UPDATED', @now)
            """,
            ("@id", Guid.NewGuid()), ("@couple", member.CoupleId), ("@connection", id), ("@item", $"seed-{Guid.NewGuid():N}"), ("@now", now));
        return id;
    }
}
