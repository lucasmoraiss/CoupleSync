using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #27 — the bills of the credit cards: mirrored by the synchronisation, used to recognise the payment of a
/// bill by its value, shown by person in <c>GET accounts</c> and added up in the "bills due" of the dashboard.
/// Pluggy is <see cref="FakePluggyServer"/>; every name and value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceBillsTests
{
    private const string Checking = FakePluggyServer.CheckingAccountId;
    private const string Card = FakePluggyServer.CreditCardAccountId;
    private const string OtherChecking = FakePluggyServer.OtherCheckingAccountId;

    private static OpenFinanceApiFactory NewFactory()
    {
        var factory = new OpenFinanceApiFactory { FastSync = true };
        // Each test says what the banks have.
        foreach (var account in new[] { Checking, Card, OtherChecking }) factory.Pluggy.Transactions[account] = [];
        return factory;
    }

    /// <summary>The day every date here is counted from, read once: a test that crosses midnight (UTC) still agrees with itself.</summary>
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    /// <summary>Noon in Brasília, ten days ago (inside the first window of a connection).</summary>
    private static DateTime Noon(int daysAfterTheBase = 0) => Today.AddDays(-10 + daysAfterTheBase).AddHours(15);

    /// <summary>The calendar day (in Brazil and in UTC alike) of <see cref="Noon"/>.</summary>
    private static DateOnly PaymentDay(int daysAfterTheBase = 0) => DateOnly.FromDateTime(Noon(daysAfterTheBase));

    private static DateOnly TodayInBrazil(OpenFinanceApiFactory factory) => DateOnly.FromDateTime(BrazilTime.ToLocal(factory.Clock.UtcNow));

    private static FakeTransaction Debit(string id, DateTime date, decimal amount, string description = "Compra Exemplo")
        => new(id, date, -amount) { Description = description };

    /// <summary>A bill that closed long ago (so it is closed whatever the day of the test).</summary>
    private static FakeBill ClosedBill(string id, DateOnly due, decimal total)
        => new(id, due, total) { ClosingDate = due.AddDays(-7), MinimumPaymentAmount = Math.Round(total / 10, 2) };

    private static void Bank(OpenFinanceApiFactory factory, string account, params FakeTransaction[] rows)
        => factory.Pluggy.Transactions[account] = rows.ToList();

    private static void Bills(OpenFinanceApiFactory factory, string account, params FakeBill[] bills)
        => factory.Pluggy.Bills[account] = bills.ToList();

    private static async Task<(Member Ana, Guid Connection)> AnaAsync(OpenFinanceApiFactory factory)
    {
        var ana = await factory.RegisterAsync("Ana Exemplo Souza");
        return (ana, await ConnectWithBankAsync(ana));
    }

    /// <summary>Someone else, in the group of <paramref name="joinCode"/> or in a new one, with the other bank: a savings account ("Banco Modelo").</summary>
    private static async Task<(Member Member, Guid Connection)> WithOtherBankAsync(OpenFinanceApiFactory factory, string name, string? joinCode)
    {
        var member = await factory.RegisterAsync(name, joinCode);
        var connection = await ConnectAsync(member, $"Bancos de {name}");
        await AddItemAsync(member, connection, FakePluggyServer.OtherItemWithAccounts);
        return (member, connection);
    }

    private static async Task DoneAsync(OpenFinanceApiFactory factory, Member member, Guid connection)
    {
        var run = await SyncAsync(factory, member, connection);
        Assert.True("Done" == (string)run["status"]!, $"{run["status"]}: {run["error_code"]} {run["error_message"]}");
    }

    private static Task<List<Dictionary<string, object?>>> BillRowsAsync(OpenFinanceApiFactory factory)
        => factory.RowsAsync("SELECT * FROM credit_card_bills ORDER BY pluggy_bill_id");

    private static decimal Money(object? value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<(string State, string? Reason)> StateAsync(OpenFinanceApiFactory factory, string pluggyId)
    {
        var row = await MirrorRowAsync(factory, pluggyId);
        return ((string)row["review_state"]!, (string?)row["auto_reason"]);
    }

    private static async Task<Guid> LineIdAsync(OpenFinanceApiFactory factory, string pluggyId)
        => Guid.Parse((string)(await MirrorRowAsync(factory, pluggyId))["id"]!);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(HttpStatusCode.OK == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    // ================================================================ the bills in the mirror

    [Fact]
    public async Task TheBillsOfACard_AreMirrored_AndSynchronisingAgainDoesNotDuplicate_TotalAndDueDateAreUpdated()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var due = TodayInBrazil(factory).AddDays(7);
        Bills(factory, Card, new FakeBill("bill-27-a", due, 1500.25m) { ClosingDate = due.AddDays(-10), MinimumPaymentAmount = 150.03m });

        await DoneAsync(factory, ana, connection);

        var stored = Assert.Single(await BillRowsAsync(factory));
        var cardId = (string)Assert.Single(await factory.RowsAsync($"SELECT id FROM bank_accounts WHERE pluggy_account_id = '{Card}'"))["id"]!;
        Assert.Equal("bill-27-a", stored["pluggy_bill_id"]);
        Assert.Equal(cardId, stored["bank_account_id"]);
        Assert.Equal(ana.CoupleId.ToString().ToUpperInvariant(), stored["couple_id"]);
        Assert.Equal(Day(due), stored["due_date"]);
        Assert.Equal(Day(due.AddDays(-10)), stored["closing_date"]);
        Assert.Equal(1500.25m, Money(stored["total_amount"]));
        Assert.Equal(150.03m, Money(stored["minimum_payment"]));
        Assert.Equal("BRL", stored["currency"]);
        Assert.Equal(1L, Convert.ToInt64(stored["is_closed"], CultureInfo.InvariantCulture));
        // The bill exactly as Pluggy sent it stays in the mirror.
        Assert.Contains(FakePluggyServer.RawOnlyMarker, (string)stored["raw_json"]!, StringComparison.Ordinal);
        // Only the card is asked for bills.
        var asked = factory.Pluggy.Requests.Where(r => r.Path == "/bills").ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, r => Assert.Contains(Card, r.Query, StringComparison.Ordinal));

        // The bank corrected the bill: the same row takes the new total and the new due date.
        Bills(factory, Card, new FakeBill("bill-27-a", due.AddDays(1), 1620.00m) { ClosingDate = due.AddDays(-10) });
        await DoneAsync(factory, ana, connection);

        var again = Assert.Single(await BillRowsAsync(factory));
        Assert.Equal(stored["id"], again["id"]);
        Assert.Equal(1620.00m, Money(again["total_amount"]));
        Assert.Equal(Day(due.AddDays(1)), again["due_date"]);
        Assert.Null(again["minimum_payment"]);
    }

    [Fact]
    public async Task ABillThePluggyListNoLongerHas_StaysInTheMirror_AndEveryPageIsRead()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        factory.Pluggy.MaxPageSize = 1;
        Bills(factory, Card,
            ClosedBill("bill-27-1", today.AddDays(-50), 100m),
            ClosedBill("bill-27-2", today.AddDays(-20), 200m),
            ClosedBill("bill-27-3", today.AddDays(10), 300m));

        await DoneAsync(factory, ana, connection);
        Assert.Equal(["bill-27-1", "bill-27-2", "bill-27-3"], (await BillRowsAsync(factory)).Select(r => (string)r["pluggy_bill_id"]!));

        Bills(factory, Card, ClosedBill("bill-27-3", today.AddDays(10), 300m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(["bill-27-1", "bill-27-2", "bill-27-3"], (await BillRowsAsync(factory)).Select(r => (string)r["pluggy_bill_id"]!));
    }

    [Fact]
    public async Task ABillListedTwiceInTheSameAnswer_IsMirroredOnce_AndTheSynchronisationEnds()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var due = TodayInBrazil(factory).AddDays(6);
        Bills(factory, Card,
            ClosedBill("bill-27-twice", due, 310.00m),
            ClosedBill("bill-27-twice", due.AddDays(1), 999.00m),
            ClosedBill("bill-27-once", due, 45.00m));

        await DoneAsync(factory, ana, connection);

        var rows = (await BillRowsAsync(factory)).ToDictionary(r => (string)r["pluggy_bill_id"]!);
        Assert.Equal(new[] { "bill-27-once", "bill-27-twice" }, rows.Keys.Order(StringComparer.Ordinal));
        // The first one of the answer is the one kept.
        Assert.Equal(310.00m, Money(rows["bill-27-twice"]["total_amount"]));
        Assert.Equal(Day(due), rows["bill-27-twice"]["due_date"]);
    }

    [Fact]
    public async Task AListOfBillsThatNeverEnds_IsReadUpToTwentyPages_AndTheSynchronisationEnds()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        // One bill per page, twenty-five pages announced: the reading stops at the twentieth.
        factory.Pluggy.MaxPageSize = 1;
        Bills(factory, Card, Enumerable.Range(1, 25).Select(i => ClosedBill($"bill-27-page-{i:D2}", today.AddDays(-i), 10m + i)).ToArray());

        await DoneAsync(factory, ana, connection);

        Assert.Equal(20, factory.Pluggy.Count("GET", "/bills"));
        Assert.Equal(
            Enumerable.Range(1, 20).Select(i => $"bill-27-page-{i:D2}"),
            (await BillRowsAsync(factory)).Select(r => (string)r["pluggy_bill_id"]!));
    }

    [Fact]
    public async Task ABill_IsClosedTheDayAfterItsClosingDate_AndWithoutAClosingDate_OnlyOnceItIsDue()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        Bills(factory, Card,
            new FakeBill("bill-closes-today", today.AddDays(7), 10m) { ClosingDate = today },
            new FakeBill("bill-closed-yesterday", today.AddDays(6), 20m) { ClosingDate = today.AddDays(-1) },
            new FakeBill("bill-no-closing-due-today", today, 30m),
            new FakeBill("bill-no-closing-due-tomorrow", today.AddDays(1), 40m));

        await DoneAsync(factory, ana, connection);

        var closed = (await BillRowsAsync(factory)).ToDictionary(
            r => (string)r["pluggy_bill_id"]!, r => Convert.ToInt64(r["is_closed"], CultureInfo.InvariantCulture) == 1);
        Assert.False(closed["bill-closes-today"]);
        Assert.True(closed["bill-closed-yesterday"]);
        Assert.True(closed["bill-no-closing-due-today"]);
        Assert.False(closed["bill-no-closing-due-tomorrow"]);
    }

    [Fact]
    public async Task TheDueDateIsTheDayWritten_WhateverTheShape_AndWhatHasNoIdDueDateOrTotalIsNotMirrored()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        factory.Pluggy.BillsBody = $$"""
            {
              "page": 1, "total": 9, "totalPages": 1,
              "results": [
                { "id": "bill-shape-utc", "dueDate": "2026-07-20T00:00:00.000Z", "totalAmount": 10.5, "totalAmountCurrencyCode": "BRL" },
                { "id": "bill-shape-local", "dueDate": "2026-07-21T00:00:00", "billClosingDate": "2026-07-14T00:00:00", "totalAmount": "20.75", "totalAmountCurrencyCode": "brl" },
                { "id": "bill-shape-day", "dueDate": "2026-07-22", "totalAmount": 30, "totalAmountCurrencyCode": null },
                { "id": "bill-shape-brasilia-midnight", "dueDate": "2026-07-23T03:00:00.000Z", "totalAmount": 40, "totalAmountCurrencyCode": "USD" },
                { "dueDate": "2026-07-24T00:00:00.000Z", "totalAmount": 50 },
                { "id": "bill-no-due", "totalAmount": 60 },
                { "id": "bill-bad-due", "dueDate": "em breve", "totalAmount": 60 },
                { "id": "bill-no-total", "dueDate": "2026-07-25T00:00:00.000Z" },
                { "id": "{{new string('x', 65)}}", "dueDate": "2026-07-26T00:00:00.000Z", "totalAmount": 70 },
                "not a bill"
              ]
            }
            """;

        await DoneAsync(factory, ana, connection);

        var rows = (await BillRowsAsync(factory)).ToDictionary(r => (string)r["pluggy_bill_id"]!);
        Assert.Equal(
            new[] { "bill-shape-brasilia-midnight", "bill-shape-day", "bill-shape-local", "bill-shape-utc" },
            rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("2026-07-20", 10.5m, "BRL"), ((string)rows["bill-shape-utc"]["due_date"]!, Money(rows["bill-shape-utc"]["total_amount"]), (string)rows["bill-shape-utc"]["currency"]!));
        Assert.Equal(("2026-07-21", 20.75m, "BRL"), ((string)rows["bill-shape-local"]["due_date"]!, Money(rows["bill-shape-local"]["total_amount"]), (string)rows["bill-shape-local"]["currency"]!));
        Assert.Equal("2026-07-14", rows["bill-shape-local"]["closing_date"]);
        // Without a currency of its own, the bill is in the currency of the card.
        Assert.Equal(("2026-07-22", 30m, "BRL"), ((string)rows["bill-shape-day"]["due_date"]!, Money(rows["bill-shape-day"]["total_amount"]), (string)rows["bill-shape-day"]["currency"]!));
        Assert.Equal(("2026-07-23", "USD"), ((string)rows["bill-shape-brasilia-midnight"]["due_date"]!, (string)rows["bill-shape-brasilia-midnight"]["currency"]!));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task WhenPluggyDoesNotGiveTheBills_TheSynchronisationStillEnds_WithTheTransactions_AndTheConnectionStaysActive(HttpStatusCode status)
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Card, new FakeTransaction("t27-card-purchase", Noon(), 80.00m) { Description = "Loja Exemplo" });
        factory.Pluggy.BillsStatus = status;

        await DoneAsync(factory, ana, connection);

        Assert.Empty(await BillRowsAsync(factory));
        Assert.Equal("Pending", (await StateAsync(factory, "t27-card-purchase")).State);
        var stored = Assert.Single(await factory.RowsAsync("SELECT status, last_sync_at_utc, last_error_code FROM bank_connections"));
        Assert.Equal("Active", stored["status"]);
        Assert.NotNull(stored["last_sync_at_utc"]);
        Assert.Null(stored["last_error_code"]);
    }

    [Fact]
    public async Task AnUnreadableListOfBills_DoesNotFailTheSynchronisation()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        factory.Pluggy.BillsBody = """{ "results": "none" }""";

        await DoneAsync(factory, ana, connection);

        Assert.Empty(await BillRowsAsync(factory));
    }

    [Fact]
    public async Task ACardThePersonTurnedOff_IsNotAskedForBills()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bills(factory, Card, ClosedBill("bill-27-off", TodayInBrazil(factory).AddDays(5), 90m));
        var cardId = (string)Assert.Single(await factory.RowsAsync($"SELECT id FROM bank_accounts WHERE pluggy_account_id = '{Card}'"))["id"]!;
        var off = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{cardId}", new { syncEnabled = false });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync());

        await DoneAsync(factory, ana, connection);

        Assert.Equal(0, factory.Pluggy.Count("GET", "/bills"));
        Assert.Empty(await BillRowsAsync(factory));
    }

    [Fact]
    public async Task ABillAlreadyMirroredUnderAnAccountOfAnotherGroup_IsNotTaken_NorDuplicated()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var (bia, _) = await WithOtherBankAsync(factory, "Bia Modelo Lima", joinCode: null); // another group
        var biaAccount = (string)Assert.Single(await factory.RowsAsync($"SELECT id FROM bank_accounts WHERE pluggy_account_id = '{OtherChecking}'"))["id"]!;
        await factory.ExecuteAsync(
            """
            INSERT INTO credit_card_bills (id, couple_id, bank_account_id, pluggy_bill_id, due_date, total_amount, currency, is_closed, raw_json, created_at_utc, updated_at_utc)
            VALUES (@id, @couple, @account, 'bill-27-taken', '2026-01-10', '11.00', 'BRL', 1, '{}', @now, @now)
            """,
            ("@id", Guid.NewGuid().ToString().ToUpperInvariant()), ("@couple", bia.CoupleId.ToString().ToUpperInvariant()), ("@account", biaAccount), ("@now", factory.Clock.UtcNow));
        Bills(factory, Card, ClosedBill("bill-27-taken", TodayInBrazil(factory).AddDays(5), 999m), ClosedBill("bill-27-mine", TodayInBrazil(factory).AddDays(5), 55m));

        await DoneAsync(factory, ana, connection);

        var rows = (await BillRowsAsync(factory)).ToDictionary(r => (string)r["pluggy_bill_id"]!);
        Assert.Equal(2, rows.Count);
        Assert.Equal(biaAccount, rows["bill-27-taken"]["bank_account_id"]);
        Assert.Equal(11.00m, Money(rows["bill-27-taken"]["total_amount"]));
        Assert.Equal(ana.CoupleId.ToString().ToUpperInvariant(), rows["bill-27-mine"]["couple_id"]);
    }

    [Fact]
    public async Task EverySynchronisation_UpdatesTheBalanceAndTheCardDataOfTheAccounts()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        await DoneAsync(factory, ana, connection);
        var before = Assert.Single(await factory.RowsAsync($"SELECT * FROM bank_accounts WHERE pluggy_account_id = '{Card}'"));
        Assert.Equal(987.65m, Money(before["balance"]));

        factory.Pluggy.AccountsJson = FakePluggyServer.DefaultAccountsJson
            .Replace("\"balance\": 987.65", "\"balance\": 1200.10", StringComparison.Ordinal)
            .Replace("\"availableCreditLimit\": 4012.35", "\"availableCreditLimit\": 3799.90", StringComparison.Ordinal)
            .Replace("\"balanceDueDate\": \"2026-10-27T00:00:00.000Z\"", "\"balanceDueDate\": \"2026-11-27T00:00:00.000Z\"", StringComparison.Ordinal)
            .Replace("\"balance\": 1234.56", "\"balance\": 2000.01", StringComparison.Ordinal);
        factory.Clock.Advance(TimeSpan.FromMinutes(30));
        await DoneAsync(factory, ana, connection);

        var card = Assert.Single(await factory.RowsAsync($"SELECT * FROM bank_accounts WHERE pluggy_account_id = '{Card}'"));
        var checking = Assert.Single(await factory.RowsAsync($"SELECT * FROM bank_accounts WHERE pluggy_account_id = '{Checking}'"));
        Assert.Equal(1200.10m, Money(card["balance"]));
        Assert.Equal(3799.90m, Money(card["available_credit_limit"]));
        Assert.Equal("2026-11-27", card["balance_due_date"]);
        Assert.Equal(2000.01m, Money(checking["balance"]));
        Assert.NotEqual(before["balance_at_utc"], card["balance_at_utc"]);
    }

    // ================================================================ the payment of a bill, by its value

    [Fact]
    public async Task ADebitEqualToTheTotalOfAClosedBill_IsIgnoredAsBillPayment_OnePaymentPerBill_AndOnlyTheExactValue()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // The checking account is read before the card: the bills arrive after the debits, in the same synchronisation.
        Bank(factory, Checking,
            Debit("t27-pays-the-bill", Noon(), 1500.25m, "Débito automático"),
            Debit("t27-same-value-later", Noon().AddHours(2), 1500.25m, "Outra coisa do mesmo valor"),
            Debit("t27-one-cent-more", Noon(), 1500.26m),
            Debit("t27-unrelated", Noon(), 42.00m));
        Bills(factory, Card, ClosedBill("bill-27-paid", PaymentDay().AddDays(3), 1500.25m));

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-pays-the-bill"));
        // One bill is paid once: the second debit of the same value is somebody's expense.
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-same-value-later"));
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-one-cent-more"));
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-unrelated"));

        // The review lists it among the ignored ones, with the reason; it is not an expense waiting.
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={BrazilTime.MonthOf(Noon())}");
        var paid = await LineIdAsync(factory, "t27-pays-the-bill");
        Assert.Contains(review.GetProperty("ignored").EnumerateArray(), l => l.GetProperty("id").GetGuid() == paid && l.GetProperty("autoReason").GetString() == "BillPayment");
        Assert.DoesNotContain(review.GetProperty("expenses").EnumerateArray(), l => l.GetProperty("id").GetGuid() == paid);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(-10, true)]
    [InlineData(-11, false)]
    [InlineData(0, true)]
    public async Task TheBillMustBeDueWithinTenDaysOfTheDebit_BeforeOrAfter(int dueDaysAfterTheDebit, bool ignored)
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t27-window", Noon(), 730.00m));
        Bills(factory, Card, new FakeBill("bill-27-window", PaymentDay().AddDays(dueDaysAfterTheDebit), 730.00m) { ClosingDate = PaymentDay().AddDays(-30) });

        await DoneAsync(factory, ana, connection);

        Assert.Equal(ignored ? ("Ignored", "BillPayment") : ("Pending", null), await StateAsync(factory, "t27-window"));
    }

    [Fact]
    public async Task OnlyAClosedBillInReaisWithAValue_AndOnlyASettledDebitInReaisOfACheckingAccount_Count()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        Bank(factory, Checking,
            Debit("t27-open-bill", Noon(), 101.00m),
            Debit("t27-dollar-bill", Noon(), 102.00m),
            Debit("t27-dollar-debit", Noon(), 103.00m) with { CurrencyCode = "USD" },
            Debit("t27-zero", Noon(), 0m),
            Debit("t27-not-settled", Noon(), 104.00m) with { Status = "PENDING" },
            new FakeTransaction("t27-credit-of-the-value", Noon(), 105.00m) { Type = "CREDIT", Description = "Entrada" },
            Debit("t27-negative-bill", Noon(), 107.00m));
        // A purchase on the card itself, of the value of a bill: a purchase, not a payment.
        Bank(factory, Card, new FakeTransaction("t27-purchase-on-card", Noon(), 106.00m) { Description = "Loja Exemplo" });
        Bills(factory, Card,
            // Still open: it closes today at the earliest.
            new FakeBill("bill-open", PaymentDay().AddDays(9), 101.00m) { ClosingDate = today },
            ClosedBill("bill-dollar", PaymentDay().AddDays(2), 102.00m) with { CurrencyCode = "USD" },
            ClosedBill("bill-for-dollar-debit", PaymentDay().AddDays(2), 103.00m),
            ClosedBill("bill-zero", PaymentDay().AddDays(2), 0m),
            ClosedBill("bill-not-settled", PaymentDay().AddDays(2), 104.00m),
            ClosedBill("bill-credit", PaymentDay().AddDays(2), 105.00m),
            ClosedBill("bill-on-card", PaymentDay().AddDays(2), 106.00m),
            ClosedBill("bill-negative", PaymentDay().AddDays(2), -107.00m));

        await DoneAsync(factory, ana, connection);

        foreach (var id in new[]
                 {
                     "t27-open-bill", "t27-dollar-bill", "t27-dollar-debit", "t27-zero", "t27-not-settled", "t27-credit-of-the-value",
                     "t27-purchase-on-card", "t27-negative-bill",
                 })
        {
            Assert.Equal((id, "Pending", (string?)null), (id, (await StateAsync(factory, id)).State, (await StateAsync(factory, id)).Reason));
        }
    }

    [Fact]
    public async Task ADebitThatArrivesInALaterSynchronisation_IsIgnoredByTheBillAlreadyMirrored()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bills(factory, Card, ClosedBill("bill-27-stored", PaymentDay().AddDays(4), 880.40m), ClosedBill("bill-27-stored-2", PaymentDay().AddDays(5), 91.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(2, (await BillRowsAsync(factory)).Count);

        // The bills do not change; the debit arrives now, in the account read BEFORE the card. On the card itself a
        // purchase of the value of the other bill arrives too: a purchase, never a payment.
        Bank(factory, Checking, Debit("t27-arrives-later", Noon(), 880.40m, "PIX enviado"), Debit("t27-other-later", Noon(), 12.00m));
        Bank(factory, Card, new FakeTransaction("t27-purchase-of-the-value", Noon(), 91.00m) { Description = "Loja Exemplo" });
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-arrives-later"));
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-other-later"));
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-purchase-of-the-value"));
    }

    [Fact]
    public async Task ABillAlreadyPaidByADebitIgnoredForItsDescription_DoesNotIgnoreAnotherDebitOfTheSameValue()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bills(factory, Card, ClosedBill("bill-27-described", PaymentDay().AddDays(4), 610.00m));
        Bank(factory, Checking, Debit("t27-described", Noon(), 610.00m) with { DescriptionRaw = "PAGAMENTO DE FATURA CARTAO" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-described"));

        Bank(factory, Checking,
            Debit("t27-described", Noon(), 610.00m) with { DescriptionRaw = "PAGAMENTO DE FATURA CARTAO" },
            Debit("t27-same-value-expense", Noon(1), 610.00m, "Aluguel"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-same-value-expense"));
    }

    [Fact]
    public async Task ABillPaidByMoneySentToTheCard_ATransfer_DoesNotIgnoreAnotherDebitOfTheSameValue()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // The card already shows the payment received (its side of the transfer) and the bill it paid.
        Bank(factory, Card, new FakeTransaction("t27-card-received", Noon(), 505.00m) { Type = "CREDIT", Description = "Pagamento recebido" });
        Bills(factory, Card, ClosedBill("bill-27-transfer", PaymentDay().AddDays(2), 505.00m));
        await DoneAsync(factory, ana, connection);

        // Now the checking account brings the debit that paid it, and another one of the same value.
        Bank(factory, Checking,
            Debit("t27-sent-to-card", Noon().AddMinutes(5), 505.00m, "Transferência"),
            Debit("t27-same-value-rent", Noon().AddHours(3), 505.00m, "Aluguel"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "Transfer"), await StateAsync(factory, "t27-sent-to-card"));
        Assert.Equal(("Ignored", "Transfer"), await StateAsync(factory, "t27-card-received"));
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-same-value-rent"));

        // And it stays so in the synchronisations that follow (the bill is known to be paid by the line already ignored).
        Bank(factory, Checking,
            Debit("t27-sent-to-card", Noon().AddMinutes(5), 505.00m, "Transferência"),
            Debit("t27-same-value-rent", Noon().AddHours(3), 505.00m, "Aluguel"),
            Debit("t27-same-value-later", Noon(1), 505.00m, "Outra conta"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-same-value-later"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBillOfACardOfAnyoneOfTheGroup_Counts_WhicheverBankIsReadFirst_AndTheBillOfAnotherGroupNever(bool cardFirst)
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory); // the card
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Modelo Reis", ana.JoinCode); // the account that pays
        Bills(factory, Card, ClosedBill("bill-27-group", PaymentDay().AddDays(5), 1990.90m));
        Bank(factory, OtherChecking, Debit("t27-bruno-pays", Noon(), 1990.90m, "Pagamento"));

        if (cardFirst)
        {
            await DoneAsync(factory, ana, anaConnection);
            await DoneAsync(factory, bruno, brunoConnection);
        }
        else
        {
            await DoneAsync(factory, bruno, brunoConnection);
            Assert.Equal(("Pending", null), await StateAsync(factory, "t27-bruno-pays"));
            await DoneAsync(factory, ana, anaConnection);
        }

        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-bruno-pays"));
    }

    [Fact]
    public async Task TheBillOfACardOfAnotherGroup_NeverIgnoresADebitOfThisOne()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory); // the card, in Ana's group
        var (carla, carlaConnection) = await WithOtherBankAsync(factory, "Carla Modelo Dias", joinCode: null); // another group
        Bills(factory, Card, ClosedBill("bill-27-other-group", PaymentDay().AddDays(5), 333.33m));
        Bank(factory, OtherChecking, Debit("t27-carla-expense", Noon(), 333.33m));

        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, carla, carlaConnection);
        // And once more with the bill changed, so that the card's side looks for payments again.
        Bills(factory, Card, ClosedBill("bill-27-other-group", PaymentDay().AddDays(6), 333.33m));
        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-carla-expense"));
    }

    [Fact]
    public async Task ADebitAlreadyIgnoredInAnotherGroup_OfTheSameValue_DoesNotCountAsThePaymentOfTheBillOfThisGroup()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory); // the card and the account that pays it
        var (carla, carlaConnection) = await WithOtherBankAsync(factory, "Carla Modelo Dias", joinCode: null); // another group
        // In Carla's group a debit of the very same value and day is already ignored (it says what it is).
        Bank(factory, OtherChecking, Debit("t27-carla-ignored", Noon(), 610.00m) with { DescriptionRaw = "PAGAMENTO DE FATURA CARTAO" });
        await DoneAsync(factory, carla, carlaConnection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-carla-ignored"));

        // Ana's bill of that value is still unpaid for Ana's group: her debit is its payment (the debit is in the
        // mirror before the bill: the account is read before the card).
        Bills(factory, Card, ClosedBill("bill-27-ana-unpaid", PaymentDay().AddDays(4), 610.00m));
        Bank(factory, Checking, Debit("t27-ana-pays", Noon(), 610.00m, "Débito automático"));
        await DoneAsync(factory, ana, anaConnection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-ana-pays"));

        // The same when the bill is already mirrored and the debit arrives later (the other half of the rule).
        Bills(factory, Card,
            ClosedBill("bill-27-ana-unpaid", PaymentDay().AddDays(4), 610.00m), ClosedBill("bill-27-ana-second", PaymentDay().AddDays(5), 720.00m));
        await DoneAsync(factory, ana, anaConnection);
        Bank(factory, OtherChecking,
            Debit("t27-carla-ignored", Noon(), 610.00m) with { DescriptionRaw = "PAGAMENTO DE FATURA CARTAO" },
            Debit("t27-carla-ignored-2", Noon(), 720.00m) with { DescriptionRaw = "PAGAMENTO DE FATURA CARTAO" });
        await DoneAsync(factory, carla, carlaConnection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-carla-ignored-2"));
        Bank(factory, Checking, Debit("t27-ana-pays", Noon(), 610.00m, "Débito automático"), Debit("t27-ana-pays-2", Noon(), 720.00m, "Débito automático"));
        await DoneAsync(factory, ana, anaConnection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-ana-pays-2"));
    }

    private static async Task SetSyncOfCardAsync(OpenFinanceApiFactory factory, Member member, bool enabled)
    {
        var cardId = (string)Assert.Single(await factory.RowsAsync($"SELECT id FROM bank_accounts WHERE pluggy_account_id = '{Card}'"))["id"]!;
        var answer = await member.Client.PatchAsJsonAsync($"{Base}/accounts/{cardId}", new { syncEnabled = enabled });
        Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheBillOfACardThePersonTurnedOff_DoesNotIgnoreADebitOfItsValue_AndDoesAgainOnceTheCardIsTurnedOn()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bills(factory, Card, ClosedBill("bill-27-card-off", PaymentDay().AddDays(4), 880.40m));
        await DoneAsync(factory, ana, connection);
        Assert.Single(await BillRowsAsync(factory));
        await SetSyncOfCardAsync(factory, ana, enabled: false);

        // The bill is stored, but nothing reads that card any more: it may be paid, changed or gone.
        Bank(factory, Checking, Debit("t27-while-off", Noon(), 880.40m, "PIX enviado"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-while-off"));
        Assert.Single(await BillRowsAsync(factory));

        // Turned on again, the bill is read again and counts again (here it changed: the debits that wait are looked at).
        await SetSyncOfCardAsync(factory, ana, enabled: true);
        Bills(factory, Card, ClosedBill("bill-27-card-off", PaymentDay().AddDays(5), 880.40m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-while-off"));
    }

    [Fact]
    public async Task ABillPaymentThePersonRestored_IsNeverIgnoredAgain_EvenWhenTheBillChanges()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t27-restored", Noon(), 450.00m));
        Bills(factory, Card, ClosedBill("bill-27-restored", PaymentDay().AddDays(3), 450.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-restored"));

        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { await LineIdAsync(factory, "t27-restored") }));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-restored"));

        Bills(factory, Card, ClosedBill("bill-27-restored", PaymentDay().AddDays(4), 450.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null), await StateAsync(factory, "t27-restored"));
    }

    [Fact]
    public async Task ALineSomeoneDiscardsWhileTheBillsAreBeingSaved_KeepsThatReview_AndTheSynchronisationEnds()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // The debits are already in the mirror, waiting, when the bills arrive.
        Bank(factory, Checking, Debit("t27-raced", Noon(), 275.00m), Debit("t27-not-raced", Noon(), 380.00m));
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t27-raced");
        Bills(factory, Card, ClosedBill("bill-27-raced", PaymentDay().AddDays(3), 275.00m), ClosedBill("bill-27-not-raced", PaymentDay().AddDays(3), 380.00m));

        var happened = 0;
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path == "/bills" && Interlocked.Exchange(ref happened, 1) == 0)
            {
                // The run saves the bill first, and then what it decided about the debits. Right before that second
                // save (the line is already ignored in the memory of the run), Ana discards the line.
                factory.BeforeNextSave = () =>
                {
                    factory.BeforeNextSave = () => factory.ExecuteAsync(
                        $"UPDATE bank_transactions SET review_state = 'Discarded' WHERE id = '{line.ToString().ToUpperInvariant()}'");
                    return Task.CompletedTask;
                };
            }

            return Task.CompletedTask;
        };

        await DoneAsync(factory, ana, connection);

        Assert.Equal(1, happened);
        Assert.Equal(("Discarded", null), await StateAsync(factory, "t27-raced"));
        // The save was refused for the line the person reviewed; the run decided again about the others, and the
        // payment of the other bill is still recognised (the bill will not "change" again for a later run to see it).
        Assert.Equal(("Ignored", "BillPayment"), await StateAsync(factory, "t27-not-raced"));
        Assert.Equal(2, (await BillRowsAsync(factory)).Count);
    }

    // ================================================================ GET accounts

    private static async Task<JsonElement> AccountsAsync(Member member)
        => await OkAsync(member.Client.GetAsync($"{Base}/accounts"));

    [Fact]
    public async Task Accounts_AreListedByPerson_AccountsWithBalance_AndCardsWithTheCurrentBillTheNextAndThePreviousOnes()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Modelo Reis", ana.JoinCode);
        var today = TodayInBrazil(factory);
        Bills(factory, Card,
            ClosedBill("bill-27-old", today.AddDays(-50), 400.00m),
            ClosedBill("bill-27-last", today.AddDays(-20), 500.00m),
            new FakeBill("bill-27-current", today, 600.00m) { ClosingDate = today.AddDays(-8), MinimumPaymentAmount = 60.00m },
            new FakeBill("bill-27-next", today.AddDays(31), 70.00m) { ClosingDate = today.AddDays(23), CurrencyCode = "USD" });
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);

        var raw = await (await ana.Client.GetAsync($"{Base}/accounts")).Content.ReadAsStringAsync();
        var people = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("people");

        // Who asks comes first.
        Assert.Equal(2, people.GetArrayLength());
        var mine = people[0];
        Assert.Equal(ana.UserId, mine.GetProperty("userId").GetGuid());
        Assert.Equal("Ana Exemplo Souza", mine.GetProperty("userName").GetString());
        Assert.True(mine.GetProperty("isMe").GetBoolean());

        var checking = Assert.Single(mine.GetProperty("accounts").EnumerateArray());
        Assert.Equal("Banco Exemplo", checking.GetProperty("bankName").GetString());
        Assert.Equal("Conta Corrente", checking.GetProperty("name").GetString());
        Assert.Equal("BANK", checking.GetProperty("type").GetString());
        Assert.Equal("CHECKING_ACCOUNT", checking.GetProperty("subtype").GetString());
        Assert.Equal("1234", checking.GetProperty("numberMasked").GetString());
        Assert.Equal("BRL", checking.GetProperty("currency").GetString());
        Assert.Equal(1234.56m, checking.GetProperty("balance").GetDecimal());
        Assert.True(checking.GetProperty("balanceAtUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-10));

        var card = Assert.Single(mine.GetProperty("cards").EnumerateArray());
        Assert.Equal("Cartão Exemplo Platinum", card.GetProperty("name").GetString());
        Assert.Equal("MASTERCARD", card.GetProperty("brand").GetString());
        Assert.Equal("5678", card.GetProperty("numberMasked").GetString());
        Assert.Equal(987.65m, card.GetProperty("balance").GetDecimal());
        Assert.Equal(5000m, card.GetProperty("creditLimit").GetDecimal());
        Assert.Equal(4012.35m, card.GetProperty("availableCreditLimit").GetDecimal());
        Assert.Equal("2026-10-20", card.GetProperty("closeDate").GetString());
        Assert.Equal("2026-10-27", card.GetProperty("dueDate").GetString());

        // The current bill: the first one due from today on (a bill due today is still to be paid).
        var current = card.GetProperty("currentBill");
        Assert.Equal(Day(today), current.GetProperty("dueDate").GetString());
        Assert.Equal(Day(today.AddDays(-8)), current.GetProperty("closingDate").GetString());
        Assert.Equal(600.00m, current.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(60.00m, current.GetProperty("minimumPayment").GetDecimal());
        Assert.Equal("BRL", current.GetProperty("currency").GetString());
        Assert.True(current.GetProperty("isClosed").GetBoolean());
        // The ones due after it, nearest first; a bill in another currency keeps its currency.
        var next = Assert.Single(card.GetProperty("nextBills").EnumerateArray());
        Assert.Equal((70.00m, "USD", false), (next.GetProperty("totalAmount").GetDecimal(), next.GetProperty("currency").GetString(), next.GetProperty("isClosed").GetBoolean()));
        // The previous ones, most recent first.
        Assert.Equal(new[] { 500.00m, 400.00m }, card.GetProperty("previousBills").EnumerateArray().Select(b => b.GetProperty("totalAmount").GetDecimal()));

        var brunos = people[1];
        Assert.Equal(bruno.UserId, brunos.GetProperty("userId").GetGuid());
        Assert.False(brunos.GetProperty("isMe").GetBoolean());
        Assert.Equal("Poupança", Assert.Single(brunos.GetProperty("accounts").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(brunos.GetProperty("cards").EnumerateArray());

        // Nothing of the raw JSON, no whole number, no credential.
        Assert.DoesNotContain(FakePluggyServer.RawOnlyMarker, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePluggyServer.CheckingAccountNumber, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePluggyServer.ClientSecret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePluggyServer.ClientId, raw, StringComparison.Ordinal);

        // The partner sees the same, with themself first.
        var seenByBruno = (await AccountsAsync(bruno)).GetProperty("people");
        Assert.Equal(new[] { bruno.UserId, ana.UserId }, seenByBruno.EnumerateArray().Select(p => p.GetProperty("userId").GetGuid()));
        Assert.True(seenByBruno[0].GetProperty("isMe").GetBoolean());
        Assert.Equal(600.00m, seenByBruno[1].GetProperty("cards")[0].GetProperty("currentBill").GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task Accounts_WithNoBillDueFromTodayOn_HaveNoCurrentBill_OnlyThePreviousOnes_AtMostTwelve()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        Bills(factory, Card, Enumerable.Range(1, 14).Select(i => ClosedBill($"bill-27-past-{i:D2}", today.AddDays(-i), 100m + i)).ToArray());
        await DoneAsync(factory, ana, connection);

        var card = (await AccountsAsync(ana)).GetProperty("people")[0].GetProperty("cards")[0];

        Assert.Equal(JsonValueKind.Null, card.GetProperty("currentBill").ValueKind);
        Assert.Empty(card.GetProperty("nextBills").EnumerateArray());
        Assert.Equal(
            Enumerable.Range(1, 12).Select(i => 100m + i),
            card.GetProperty("previousBills").EnumerateArray().Select(b => b.GetProperty("totalAmount").GetDecimal()));
    }

    [Fact]
    public async Task Accounts_AreOnlyTheOnesOfTheGroupOfTheToken()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        Bills(factory, Card, ClosedBill("bill-27-ana", TodayInBrazil(factory).AddDays(5), 123.45m));
        await DoneAsync(factory, ana, anaConnection);
        var (carla, carlaConnection) = await WithOtherBankAsync(factory, "Carla Modelo Dias", joinCode: null); // another group
        await DoneAsync(factory, carla, carlaConnection);
        var dora = await factory.RegisterAsync("Dora Modelo"); // a group with nothing connected

        var carlas = await (await carla.Client.GetAsync($"{Base}/accounts")).Content.ReadAsStringAsync();
        var person = Assert.Single(JsonSerializer.Deserialize<JsonElement>(carlas).GetProperty("people").EnumerateArray());
        Assert.Equal(carla.UserId, person.GetProperty("userId").GetGuid());
        Assert.Equal("Poupança", Assert.Single(person.GetProperty("accounts").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(person.GetProperty("cards").EnumerateArray());
        Assert.DoesNotContain("Cartão Exemplo", carlas, StringComparison.Ordinal);
        Assert.DoesNotContain("123.45", carlas, StringComparison.Ordinal);
        Assert.DoesNotContain(ana.UserId.ToString(), carlas, StringComparison.OrdinalIgnoreCase);

        Assert.Empty((await AccountsAsync(dora)).GetProperty("people").EnumerateArray());

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Base}/accounts")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhoLeavesTheGroup_TakesTheBillsToo_AndTheOthersStopSeeingThem(bool removedByTheOwner)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana Exemplo Souza"); // the owner, with the other bank
        var anaConnection = await ConnectAsync(ana);
        await AddItemAsync(ana, anaConnection, FakePluggyServer.OtherItemWithAccounts);
        var bruno = await factory.RegisterAsync("Bruno Modelo Reis", ana.JoinCode); // the card
        var brunoConnection = await ConnectWithBankAsync(bruno);
        Bills(factory, Card, ClosedBill("bill-27-leaves", TodayInBrazil(factory).AddDays(5), 700.00m));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        Assert.Single(await BillRowsAsync(factory));
        Assert.Equal(700.00m, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());

        var gone = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(gone.IsSuccessStatusCode, await gone.Content.ReadAsStringAsync());

        Assert.Empty(await BillRowsAsync(factory));
        Assert.Empty(await factory.RowsAsync($"SELECT id FROM bank_accounts WHERE pluggy_account_id = '{Card}'"));
        var person = Assert.Single((await AccountsAsync(ana)).GetProperty("people").EnumerateArray());
        Assert.Equal(ana.UserId, person.GetProperty("userId").GetGuid());
        Assert.Empty(person.GetProperty("cards").EnumerateArray());
        var upcoming = (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        Assert.Equal(0m, upcoming.GetProperty("totalBrl").GetDecimal());
        Assert.Empty(upcoming.GetProperty("bills").EnumerateArray());
    }

    // ================================================================ the dashboard: bills due in the next 30 days

    [Fact]
    public async Task UpcomingBills_AddOnlyTheBillsOfTheGroup_DueFromTodayToThirtyDaysAhead_AndOnlyReais()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        Bills(factory, Card,
            ClosedBill("bill-due-yesterday", today.AddDays(-1), 1000.00m),
            ClosedBill("bill-due-today", today, 100.10m),
            new FakeBill("bill-due-in-30", today.AddDays(30), 200.20m) { ClosingDate = today.AddDays(20) },
            new FakeBill("bill-due-in-31", today.AddDays(31), 4000.00m),
            ClosedBill("bill-in-dollars", today.AddDays(3), 50.00m) with { CurrencyCode = "USD" },
            ClosedBill("bill-zero", today.AddDays(4), 0m),
            ClosedBill("bill-negative", today.AddDays(5), -30.00m));
        await DoneAsync(factory, ana, anaConnection);
        // Another group, with a bill of its own in the same days (stored straight in the table: the fixture has one card).
        var (carla, carlaConnection) = await WithOtherBankAsync(factory, "Carla Modelo Dias", joinCode: null);
        await DoneAsync(factory, carla, carlaConnection);
        var carlaAccount = (string)Assert.Single(await factory.RowsAsync($"SELECT id FROM bank_accounts WHERE pluggy_account_id = '{OtherChecking}'"))["id"]!;
        await factory.ExecuteAsync(
            """
            INSERT INTO credit_card_bills (id, couple_id, bank_account_id, pluggy_bill_id, due_date, total_amount, currency, is_closed, raw_json, created_at_utc, updated_at_utc)
            VALUES (@id, @couple, @account, 'bill-of-carla', @due, '77777.00', 'BRL', 1, '{}', @now, @now)
            """,
            ("@id", Guid.NewGuid().ToString().ToUpperInvariant()), ("@couple", carla.CoupleId.ToString().ToUpperInvariant()), ("@account", carlaAccount),
            ("@due", Day(today.AddDays(2))), ("@now", factory.Clock.UtcNow));

        var raw = await (await ana.Client.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();
        var upcoming = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("upcomingBills");

        // Only reais, only what is owed: the bill in dollars, the one of zero and the credit are listed, never added.
        Assert.Equal(300.30m, upcoming.GetProperty("totalBrl").GetDecimal());
        Assert.Equal(Day(today), upcoming.GetProperty("fromDay").GetString());
        Assert.Equal(Day(today.AddDays(30)), upcoming.GetProperty("toDay").GetString());
        var bills = upcoming.GetProperty("bills").EnumerateArray().ToList();
        Assert.Equal(
            new[] { (Day(today), 100.10m, "BRL"), (Day(today.AddDays(3)), 50.00m, "USD"), (Day(today.AddDays(4)), 0m, "BRL"), (Day(today.AddDays(5)), -30.00m, "BRL"), (Day(today.AddDays(30)), 200.20m, "BRL") },
            bills.Select(b => (b.GetProperty("dueDate").GetString()!, b.GetProperty("totalAmount").GetDecimal(), b.GetProperty("currency").GetString()!)));
        var first = bills[0];
        Assert.Equal("Cartão Exemplo Platinum", first.GetProperty("cardName").GetString());
        Assert.Equal("Banco Exemplo", first.GetProperty("bankName").GetString());
        Assert.Equal(ana.UserId, first.GetProperty("userId").GetGuid());
        Assert.Equal("Ana Exemplo Souza", first.GetProperty("userName").GetString());
        Assert.True(first.GetProperty("isClosed").GetBoolean());
        Assert.DoesNotContain("77777", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePluggyServer.RawOnlyMarker, raw, StringComparison.Ordinal);

        // Carla's dashboard has hers and nothing of Ana's.
        var carlas = (await carla.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        Assert.Equal(77777.00m, carlas.GetProperty("totalBrl").GetDecimal());
        Assert.Single(carlas.GetProperty("bills").EnumerateArray());
    }

    [Fact]
    public async Task TheBillsOfSomeoneWhoDisconnected_AreNoLongerDue_NorTheirAccountsShown_WhileTheOthersStayConnected()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory); // the card
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Modelo Reis", ana.JoinCode);
        Bills(factory, Card, ClosedBill("bill-27-disconnected", TodayInBrazil(factory).AddDays(5), 700.00m));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        Assert.Equal(700.00m, (await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{anaConnection}")).StatusCode);

        // Nothing reads Ana's card any more: its bill would only grow old. Bruno is still connected, so the card of
        // the dashboard stays, with nothing due.
        var upcoming = (await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        Assert.Equal(0m, upcoming.GetProperty("totalBrl").GetDecimal());
        Assert.Empty(upcoming.GetProperty("bills").EnumerateArray());
        var person = Assert.Single((await AccountsAsync(ana)).GetProperty("people").EnumerateArray());
        Assert.Equal(bruno.UserId, person.GetProperty("userId").GetGuid());
        // The mirror itself stays (disconnecting deletes nothing).
        Assert.Single(await BillRowsAsync(factory));
    }

    [Fact]
    public async Task TheBillsOfACardThePersonTurnedOff_AreNotDueNorShownInTheAccounts_NothingIsDeleted_AndTurnedOnTheyAreBackAndUpdated()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var today = TodayInBrazil(factory);
        Bills(factory, Card, ClosedBill("bill-27-off-a-past", today.AddDays(-20), 500.00m), ClosedBill("bill-27-off-b-due", today.AddDays(5), 700.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(700.00m, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());

        await SetSyncOfCardAsync(factory, ana, enabled: false);

        // Like a disconnected connection: nothing reads the card, its bills would only grow old. Ana is still
        // connected, so the card of the dashboard stays, with nothing due.
        var upcoming = (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        Assert.Equal(0m, upcoming.GetProperty("totalBrl").GetDecimal());
        Assert.Empty(upcoming.GetProperty("bills").EnumerateArray());
        // The card itself is still listed in the accounts, with no bill at all.
        var card = Assert.Single((await AccountsAsync(ana)).GetProperty("people")[0].GetProperty("cards").EnumerateArray());
        Assert.Equal("Cartão Exemplo Platinum", card.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("currentBill").ValueKind);
        Assert.Empty(card.GetProperty("nextBills").EnumerateArray());
        Assert.Empty(card.GetProperty("previousBills").EnumerateArray());
        // Nothing is deleted, and a synchronisation with the card off changes nothing of it.
        Bills(factory, Card, ClosedBill("bill-27-off-a-past", today.AddDays(-20), 500.00m), ClosedBill("bill-27-off-b-due", today.AddDays(5), 850.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(new[] { 500.00m, 700.00m }, (await BillRowsAsync(factory)).Select(r => Money(r["total_amount"])));
        Assert.Empty((await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("bills").EnumerateArray());

        // Turned on again: what was stored is shown right away...
        await SetSyncOfCardAsync(factory, ana, enabled: true);
        Assert.Equal(700.00m, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());
        card = (await AccountsAsync(ana)).GetProperty("people")[0].GetProperty("cards")[0];
        Assert.Equal(700.00m, card.GetProperty("currentBill").GetProperty("totalAmount").GetDecimal());
        Assert.Equal(500.00m, Assert.Single(card.GetProperty("previousBills").EnumerateArray()).GetProperty("totalAmount").GetDecimal());
        // ...and the next synchronisation updates it.
        await DoneAsync(factory, ana, connection);
        Assert.Equal(850.00m, (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills").GetProperty("totalBrl").GetDecimal());
        Assert.Equal(850.00m, (await AccountsAsync(ana)).GetProperty("people")[0].GetProperty("cards")[0].GetProperty("currentBill").GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task TheThirtyDays_AreCountedFromTheDayInBrazil_NotFromTheDayInUtc()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // 01:30 UTC of some day: still 22:30 of the day before in Brasília.
        var utcDay = DateTime.UtcNow.Date.AddDays(1);
        factory.Clock.SetNow(utcDay.AddHours(1).AddMinutes(30));
        var todayInBrazil = DateOnly.FromDateTime(utcDay).AddDays(-1);
        Bills(factory, Card,
            ClosedBill("bill-brazil-today", todayInBrazil, 10.00m),
            // It closes today in Brazil (yesterday, if the day were the one in UTC): still open.
            new FakeBill("bill-brazil-30", todayInBrazil.AddDays(30), 20.00m) { ClosingDate = todayInBrazil },
            // Thirty days ahead of the day in UTC, thirty-one of the day in Brazil.
            new FakeBill("bill-brazil-31", todayInBrazil.AddDays(31), 4000.00m));
        await DoneAsync(factory, ana, connection);

        var upcoming = (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");

        Assert.Equal(30.00m, upcoming.GetProperty("totalBrl").GetDecimal());
        Assert.Equal(Day(todayInBrazil), upcoming.GetProperty("fromDay").GetString());
        // The synchronisation decides "closed" by the same day...
        var closed = (await BillRowsAsync(factory)).ToDictionary(
            r => (string)r["pluggy_bill_id"]!, r => Convert.ToInt64(r["is_closed"], CultureInfo.InvariantCulture) == 1);
        Assert.False(closed["bill-brazil-30"]);
        // ...and so does the screen of the accounts: the bill due today in Brazil is the current one, not a previous one.
        var card = (await AccountsAsync(ana)).GetProperty("people")[0].GetProperty("cards")[0];
        Assert.Equal(Day(todayInBrazil), card.GetProperty("currentBill").GetProperty("dueDate").GetString());
        Assert.Empty(card.GetProperty("previousBills").EnumerateArray());
    }

    [Fact]
    public async Task TheDashboard_HasNoUpcomingBillsWithoutAConnection_AndKeepsEveryFieldItAlwaysHad()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana Exemplo Souza");

        var before = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard");

        Assert.True(!before.TryGetProperty("upcomingBills", out var none) || none.ValueKind == JsonValueKind.Null);
        foreach (var field in new[] { "totalExpenses", "expensesByCategory", "partnerBreakdown", "transactionCount", "periodStart", "periodEnd", "generatedAtUtc" })
            Assert.True(before.TryGetProperty(field, out _), field);

        // Connected, with no card bill yet: the card of the dashboard shows zero.
        var connection = await ConnectWithBankAsync(ana);
        await DoneAsync(factory, ana, connection);
        var connected = (await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard")).GetProperty("upcomingBills");
        Assert.Equal(0m, connected.GetProperty("totalBrl").GetDecimal());
        Assert.Empty(connected.GetProperty("bills").EnumerateArray());
        Assert.Single((await AccountsAsync(ana)).GetProperty("people").EnumerateArray());

        // Disconnected: nobody of the group is connected any more, the card goes away, and so do the accounts
        // (nothing reads them any more: their balances would only grow old).
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connection}")).StatusCode);
        Assert.Empty((await AccountsAsync(ana)).GetProperty("people").EnumerateArray());
        var after = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard");
        Assert.True(!after.TryGetProperty("upcomingBills", out var gone) || gone.ValueKind == JsonValueKind.Null);
        foreach (var field in new[] { "totalExpenses", "expensesByCategory", "partnerBreakdown", "transactionCount", "periodStart", "periodEnd", "generatedAtUtc" })
            Assert.True(after.TryGetProperty(field, out _), field);
    }
}
