using System.Net;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Dashboard;
using CoupleSync.Application.Income;
using CoupleSync.Application.OpenFinance;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using CoupleSync.TestSupport;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.OpenFinance;

/// <summary>
/// Issue #27 — the bills of the cards and the entries that become incomes, without a database: the rule that says a
/// debit pays a bill, when a bill counts as closed, how the Pluggy client reads <c>GET /bills</c>, what the mirror
/// row does when it becomes an income, the bills due of the dashboard and what deleting an income does. Every name
/// and value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceBillsAndIncomesTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 3);

    private static MoneyMovement Out(decimal amount, DateOnly? day = null, string? currency = "BRL")
        => new(amount, currency, day ?? Day, MoneyDirection.Out, Guid.NewGuid());

    private static CardBillFacts Bill(decimal total, int dueDaysAfter = 0, bool closed = true, string? currency = "BRL", Guid? id = null)
        => new(id ?? Guid.NewGuid(), total, currency, Day.AddDays(dueDaysAfter), closed);

    // ---------------------------------------------------------------- the rule: a debit pays a bill

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(-10, true)]
    [InlineData(11, false)]
    [InlineData(-11, false)]
    public void ADebitPaysABill_DueAtMostTenDaysBeforeOrAfterIt(int dueDaysAfter, bool expected)
        => Assert.Equal(expected, BankReconciliationRules.PaysBill(Out(900.10m), Bill(900.10m, dueDaysAfter)));

    [Fact]
    public void PayingABill_NeedsEveryOneOfItsConditions()
    {
        Assert.True(BankReconciliationRules.PaysBill(Out(900.10m), Bill(900.10m)));
        // one cent away
        Assert.False(BankReconciliationRules.PaysBill(Out(900.10m), Bill(900.11m)));
        Assert.False(BankReconciliationRules.PaysBill(Out(900.11m), Bill(900.10m)));
        // a bill still open: its total may still change
        Assert.False(BankReconciliationRules.PaysBill(Out(900.10m), Bill(900.10m, closed: false)));
        // another currency, on either side: nothing is compared as if it were reais
        Assert.False(BankReconciliationRules.PaysBill(Out(900.10m), Bill(900.10m, currency: "USD")));
        Assert.False(BankReconciliationRules.PaysBill(Out(900.10m, currency: "USD"), Bill(900.10m)));
        Assert.False(BankReconciliationRules.PaysBill(Out(900.10m), Bill(900.10m, currency: null)));
        // nothing to pay
        Assert.False(BankReconciliationRules.PaysBill(Out(0m), Bill(0m)));
        Assert.False(BankReconciliationRules.PaysBill(Out(30m), Bill(-30m)));
        // money that came in pays nothing
        Assert.False(BankReconciliationRules.PaysBill(new MoneyMovement(900.10m, "BRL", Day, MoneyDirection.In, Guid.NewGuid()), Bill(900.10m)));
    }

    [Theory]
    [InlineData("BANK", true)]
    [InlineData(" bank ", true)]
    [InlineData("CREDIT", false)]
    [InlineData("INVESTMENT", false)]
    [InlineData(null, false)]
    public void OnlyMoneyLeavingACheckingOrSavingsAccount_CanPayABill(string? accountType, bool expected)
    {
        Assert.Equal(expected, BankReconciliationRules.CanPayABill(Out(100m), accountType));
        Assert.False(BankReconciliationRules.CanPayABill(new MoneyMovement(100m, "BRL", Day, MoneyDirection.In), accountType));
        Assert.False(BankReconciliationRules.CanPayABill(Out(0m), accountType));
        Assert.False(BankReconciliationRules.CanPayABill(Out(100m, currency: "USD"), accountType));
    }

    [Theory]
    [InlineData("CREDIT", true)]
    [InlineData(" credit ", true)]
    [InlineData("BANK", false)]
    [InlineData(null, false)]
    public void ACreditCard_IsTheAccountOfTypeCredit(string? accountType, bool expected)
        => Assert.Equal(expected, BankReconciliationRules.IsCreditCard(accountType));

    [Fact]
    public void TheBillPaid_IsTheOneDueNearest_ThenTheOneDueFirst_ThenTheSmallestId_AndNoneWhenNonePays()
    {
        var far = Bill(500m, dueDaysAfter: 8);
        var near = Bill(500m, dueDaysAfter: -2);
        var sameDistanceLater = Bill(500m, dueDaysAfter: 2);
        var twinLow = Bill(500m, dueDaysAfter: -2, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var twinHigh = Bill(500m, dueDaysAfter: -2, id: Guid.Parse("ffffffff-0000-0000-0000-000000000001"));

        Assert.Same(near, BankReconciliationRules.FindBillPaid(Out(500m), [far, sameDistanceLater, near]));
        Assert.Same(twinLow, BankReconciliationRules.FindBillPaid(Out(500m), [twinHigh, twinLow]));
        Assert.Null(BankReconciliationRules.FindBillPaid(Out(500m), [Bill(499m), Bill(500m, closed: false), Bill(500m, dueDaysAfter: 30)]));
        Assert.Null(BankReconciliationRules.FindBillPaid(Out(500m), []));
    }

    // ---------------------------------------------------------------- the bill of the mirror

    private static CreditCardBillSnapshot Snapshot(
        DateOnly? due = null, DateOnly? closing = null, decimal total = 1500.25m, string? currency = "BRL", decimal? minimum = 150m, string raw = """{"id":"b-1"}""")
        => new(due ?? new DateOnly(2026, 10, 27), closing, total, currency, minimum, raw);

    private static readonly DateOnly Today = new(2026, 10, 7);

    [Theory]
    // With a closing date: closed from the day after it on.
    [InlineData("2026-10-06", "2026-10-27", true)]
    [InlineData("2026-10-07", "2026-10-27", false)]
    [InlineData("2026-10-08", "2026-10-27", false)]
    // Without one: only once the bill is due.
    [InlineData(null, "2026-10-06", true)]
    [InlineData(null, "2026-10-07", true)]
    [InlineData(null, "2026-10-08", false)]
    public void ABill_IsClosedOnlyWhenThatIsCertain(string? closing, string due, bool expected)
        => Assert.Equal(expected, CreditCardBill.IsClosedOn(closing is null ? null : DateOnly.Parse(closing, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture), Today));

    [Fact]
    public void ANewBill_TakesWhatPluggySays_AndTheCurrencyOfTheCardWhenItNamesNone()
    {
        var couple = Guid.NewGuid();
        var account = Guid.NewGuid();

        var bill = CreditCardBill.Create(couple, account, " b-1 ", Snapshot(closing: new DateOnly(2026, 10, 1), currency: " brl "), "USD", Today, Now);
        var unnamed = CreditCardBill.Create(couple, account, "b-2", Snapshot(currency: null), "usd", Today, Now);
        var nobodySays = CreditCardBill.Create(couple, account, "b-3", Snapshot(currency: " "), null, Today, Now);

        Assert.Equal(("b-1", couple, account), (bill.PluggyBillId, bill.CoupleId, bill.BankAccountId));
        Assert.Equal((new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 1), 1500.25m, 150m), (bill.DueDate, bill.ClosingDate!.Value, bill.TotalAmount, bill.MinimumPayment!.Value));
        Assert.Equal("BRL", bill.Currency);
        Assert.True(bill.IsClosed);
        Assert.Equal("""{"id":"b-1"}""", bill.RawJson);
        Assert.Equal((Now, Now), (bill.CreatedAtUtc, bill.UpdatedAtUtc));
        // A bill in the currency of a card in dollars is in dollars: never assumed to be reais.
        Assert.Equal("USD", unnamed.Currency);
        Assert.Equal("BRL", nobodySays.Currency);
        Assert.Equal("CreditCardBillSnapshot", Snapshot().ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABillWithoutAnId_OrWithOneTooLong_IsRefused(string id)
    {
        Assert.Throws<ArgumentException>(() => CreditCardBill.Create(Guid.NewGuid(), Guid.NewGuid(), id, Snapshot(), "BRL", Today, Now));
        Assert.Throws<ArgumentException>(() => CreditCardBill.Create(Guid.NewGuid(), Guid.NewGuid(), new string('x', 65), Snapshot(), "BRL", Today, Now));
    }

    [Fact]
    public void Refresh_SaysWhetherWhatAPaymentIsRecognisedByChanged()
    {
        var bill = CreditCardBill.Create(Guid.NewGuid(), Guid.NewGuid(), "b-1", Snapshot(), "BRL", Today, Now);
        var later = Now.AddHours(1);

        // The same again, or only the minimum payment and the raw JSON: nothing a payment is recognised by.
        Assert.False(bill.Refresh(Snapshot(), "BRL", Today, later));
        Assert.False(bill.Refresh(Snapshot(minimum: null, raw: " "), "BRL", Today, later));
        Assert.Null(bill.MinimumPayment);
        Assert.Equal("{}", bill.RawJson);
        Assert.Equal(later, bill.UpdatedAtUtc);

        Assert.True(bill.Refresh(Snapshot(total: 1600m), "BRL", Today, later));
        Assert.True(bill.Refresh(Snapshot(total: 1600m, due: new DateOnly(2026, 10, 28)), "BRL", Today, later));
        Assert.True(bill.Refresh(Snapshot(total: 1600m, due: new DateOnly(2026, 10, 28), currency: "USD"), "BRL", Today, later));
        Assert.False(bill.IsClosed);
        // The day passes and the bill closes: that alone is a change.
        Assert.True(bill.Refresh(Snapshot(total: 1600m, due: new DateOnly(2026, 10, 28), currency: "USD"), "BRL", new DateOnly(2026, 10, 28), later));
        Assert.True(bill.IsClosed);
    }

    // ---------------------------------------------------------------- the Pluggy client: GET /bills

    private static PluggyHttpClient Client(FakePluggyServer pluggy) => new(
        new SingleHandlerFactory(pluggy),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }),
        NullLogger<PluggyHttpClient>.Instance);

    private static PluggyAuth Auth() => PluggyAuth.Of(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret);

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(PluggyHttpClient.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    [Fact]
    public async Task GetBills_AsksForTheAccount_AndReadsEveryField_AsCalendarDays()
    {
        var pluggy = new FakePluggyServer();
        pluggy.Bills[FakePluggyServer.CreditCardAccountId] =
        [
            new FakeBill("bill-1", new DateOnly(2026, 10, 27), 1500.25m) { ClosingDate = new DateOnly(2026, 10, 20), MinimumPaymentAmount = 150.03m },
            new FakeBill("bill-2", new DateOnly(2026, 9, 27), 80m) { CurrencyCode = "USD" },
        ];

        var bills = await Client(pluggy).GetBillsAsync(Auth(), FakePluggyServer.CreditCardAccountId, CancellationToken.None);

        var asked = Assert.Single(pluggy.Requests, r => r.Path == "/bills");
        Assert.Equal("GET", asked.Method);
        Assert.Equal($"?accountId={FakePluggyServer.CreditCardAccountId}&pageSize=500&page=1", asked.Query);
        Assert.Equal(2, bills.Count);
        Assert.Equal(
            ("bill-1", new DateOnly(2026, 10, 27), (DateOnly?)new DateOnly(2026, 10, 20), 1500.25m, "BRL", (decimal?)150.03m),
            (bills[0].Id, bills[0].DueDate, bills[0].ClosingDate, bills[0].TotalAmount, bills[0].CurrencyCode, bills[0].MinimumPayment));
        Assert.Equal(("bill-2", (DateOnly?)null, "USD", (decimal?)null), (bills[1].Id, bills[1].ClosingDate, bills[1].CurrencyCode, bills[1].MinimumPayment));
        Assert.Contains(FakePluggyServer.RawOnlyMarker, bills[0].RawJson, StringComparison.Ordinal);
        // Nothing of the bill is printed by accident.
        Assert.Equal("PluggyBill", bills[0].ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetBills_ReadsEveryPageUntilTheLast(bool withoutTotalPages)
    {
        var pluggy = new FakePluggyServer { MaxPageSize = 2, OmitTotalPages = withoutTotalPages };
        pluggy.Bills[FakePluggyServer.CreditCardAccountId] =
            Enumerable.Range(1, 5).Select(i => new FakeBill($"bill-{i}", new DateOnly(2026, i, 10), 10m * i)).ToList();

        var bills = await Client(pluggy).GetBillsAsync(Auth(), FakePluggyServer.CreditCardAccountId, CancellationToken.None);

        // Without a page count the client stops at the first page that is not full: the server of the test gives
        // two per page whatever is asked, so it reads only that page (as with the transactions).
        Assert.Equal(withoutTotalPages ? 2 : 5, bills.Count);
        Assert.Equal(withoutTotalPages ? 1 : 3, pluggy.Count("GET", "/bills"));
    }

    [Fact]
    public async Task GetBills_OfACardWithoutBills_IsAnEmptyList_AndABodyThatIsNotAListIsAFailure()
    {
        var pluggy = new FakePluggyServer();
        Assert.Empty(await Client(pluggy).GetBillsAsync(Auth(), FakePluggyServer.CreditCardAccountId, CancellationToken.None));

        pluggy.BillsBody = """{ "results": 3 }""";
        var failure = await Assert.ThrowsAsync<PluggyException>(() => Client(pluggy).GetBillsAsync(Auth(), FakePluggyServer.CreditCardAccountId, CancellationToken.None));
        Assert.Equal(PluggyErrorCodes.Unavailable, failure.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, PluggyErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, PluggyErrorCodes.InvalidCredentials)]
    public async Task GetBills_TurnsAFailureOfPluggyIntoOneOfTheClosedCodes(HttpStatusCode status, string code)
    {
        var pluggy = new FakePluggyServer { BillsStatus = status };

        var failure = await Assert.ThrowsAsync<PluggyException>(() => Client(pluggy).GetBillsAsync(Auth(), FakePluggyServer.CreditCardAccountId, CancellationToken.None));

        Assert.Equal(code, failure.Code);
    }

    // ---------------------------------------------------------------- the mirror row that becomes an income

    private static BankTransaction Entry(decimal amount = 3500m, string? type = "CREDIT")
        => BankTransaction.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "t-in",
            new BankTransactionSnapshot(
                new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc), amount, type, "BRL", "Pagamento Empresa Exemplo", null, null, null, null, null,
                null, null, null, null, null, "POSTED", null, "{}"),
            suggestedCategory: null, Guid.NewGuid(), Now);

    [Fact]
    public void AnEntryRegisteredAsIncome_IsConfirmed_PointingToTheIncome_AndDiscardOrRestoreClearsTheLink()
    {
        var income = Guid.NewGuid();
        var row = Entry();

        row.ConfirmAsIncome(income, Now);

        Assert.Equal(BankTransactionReviewState.Confirmed, row.ReviewState);
        Assert.Equal(income, row.LinkedIncomeSourceId);
        Assert.Null(row.LinkedTransactionId);
        Assert.Equal(Now, row.ReviewedAtUtc);
        Assert.False(row.CountsAsDiscarded);
        Assert.False(row.OpenToAutomaticRules);

        // The income was deleted: the line goes back as discarded, with no link.
        row.Discard(Now.AddMinutes(1));
        Assert.Equal(BankTransactionReviewState.Discarded, row.ReviewState);
        Assert.Null(row.LinkedIncomeSourceId);
        Assert.True(row.CountsAsDiscarded);

        var again = Entry();
        again.ConfirmAsIncome(income, Now);
        again.Restore(Now.AddMinutes(1));
        Assert.Equal(BankTransactionReviewState.Pending, again.ReviewState);
        Assert.Null(again.LinkedIncomeSourceId);
    }

    [Fact]
    public void OnlyAnEntryConfirmedWithoutItsIncome_LostItsIncome()
    {
        var row = Entry();
        Assert.False(row.LostItsIncome); // waiting
        row.ConfirmAsIncome(Guid.NewGuid(), Now);
        Assert.False(row.LostItsIncome); // registered, with its income

        // An expense confirmed points to a transaction, never to an income: it lost nothing.
        var expense = Entry(-25m, "DEBIT");
        expense.Confirm(Guid.NewGuid(), Now);
        Assert.False(expense.LostItsIncome);
        Assert.False(expense.CountsAsDiscarded);
    }

    [Theory]
    [InlineData(BankTransactionStatus.Posted, 100, "BRL", true)]
    [InlineData(BankTransactionStatus.Pending, 100, "BRL", false)]
    [InlineData(BankTransactionStatus.Posted, 0, "BRL", false)]
    [InlineData(BankTransactionStatus.Posted, 100, "USD", false)]
    [InlineData(BankTransactionStatus.Posted, 100, null, false)]
    public void AnEntryCanBecomeAnIncome_OnlySettled_WithAValue_InReais(BankTransactionStatus status, double amount, string? currency, bool expected)
        => Assert.Equal(expected, BankTransaction.CanBecomeIncome(status, (decimal)amount, currency));

    // ---------------------------------------------------------------- the dashboard: bills due

    private static UpcomingBill Due(DateOnly day, decimal total, string currency = "BRL", string card = "Cartão Exemplo")
        => new(Guid.NewGuid(), Guid.NewGuid(), card, "Banco Exemplo", Guid.NewGuid(), "Ana Exemplo", day, total, currency, true);

    [Fact]
    public async Task TheDashboard_AsksForTheBillsOfTodayInBrazilAndTheThirtyDaysAfter_AndAddsOnlyWhatIsOwedInReais()
    {
        var couple = Guid.NewGuid();
        // 01:30 UTC of the 8th: still the 7th in Brasília.
        var clock = new FixedDateTimeProvider(new DateTime(2026, 10, 8, 1, 30, 0, DateTimeKind.Utc));
        var today = new DateOnly(2026, 10, 7);
        var repo = new FakeDashboardRepository
        {
            Bills =
            [
                Due(today.AddDays(30), 200.20m, card: "Cartão B"),
                Due(today, 100.10m),
                Due(today.AddDays(3), 50m, "USD"),
                Due(today.AddDays(4), 0m),
                Due(today.AddDays(5), -30m),
                Due(today.AddDays(3), 7m, " brl ", card: "Cartão A"),
                Due(today.AddDays(-1), 9999m),
                Due(today.AddDays(31), 9999m),
            ],
        };

        var result = await new GetDashboardQueryHandler(repo, clock).HandleAsync(new GetDashboardQuery(couple, null, null), CancellationToken.None);

        Assert.Equal((today, today.AddDays(30)), Assert.Single(repo.BillWindowsAsked));
        var upcoming = result.UpcomingBills!;
        Assert.Equal((today, today.AddDays(30)), (upcoming.FromDay, upcoming.ToDay));
        Assert.Equal(307.30m, upcoming.TotalBrl);
        // Earliest first; on the same day, by the name of the card.
        Assert.Equal(new[] { 100.10m, 7m, 50m, 0m, -30m, 200.20m }, upcoming.Bills.Select(b => b.TotalAmount));
        Assert.Equal("UpcomingBill", upcoming.Bills[0].ToString());
    }

    [Fact]
    public async Task TheDashboard_HasNoBillsDue_WhenNobodyOfTheGroupIsConnected_AndZeroWhenConnectedWithoutBills_WhateverThePeriodAsked()
    {
        var clock = new FixedDateTimeProvider(Now);
        var none = await new GetDashboardQueryHandler(new FakeDashboardRepository(), clock)
            .HandleAsync(new GetDashboardQuery(Guid.NewGuid(), null, null), CancellationToken.None);
        Assert.Null(none.UpcomingBills);

        var repo = new FakeDashboardRepository { Bills = [] };
        var empty = await new GetDashboardQueryHandler(repo, clock)
            .HandleAsync(new GetDashboardQuery(Guid.NewGuid(), new DateTime(2026, 1, 1), new DateTime(2026, 1, 31)), CancellationToken.None);
        Assert.Equal(0m, empty.UpcomingBills!.TotalBrl);
        Assert.Empty(empty.UpcomingBills.Bills);
        // The period asked for the expenses does not move the thirty days of the bills.
        Assert.Equal((new DateOnly(2026, 10, 7), new DateOnly(2026, 11, 6)), Assert.Single(repo.BillWindowsAsked));
    }

    // ---------------------------------------------------------------- incomes: the mark "from the bank" and the delete

    private static (IncomeService Service, FakeIncomeSourceRepository Repo, Couple Couple, User Owner) Incomes()
    {
        var repo = new FakeIncomeSourceRepository();
        var couples = new FakeCoupleRepository();
        var couple = Couple.Create("ABC123", Now);
        var owner = User.Create(EmailAddress.From("ana@example.com"), "Ana Exemplo", "hash", Now);
        couple.AddMember(owner, Now);
        couples.Couples.Add(couple);
        return (new IncomeService(repo, couples, new FixedDateTimeProvider(Now)), repo, couple, owner);
    }

    [Fact]
    public async Task TheIncomesOfAMonth_SayWhichCameFromTheBank_AndAnUpdateKeepsSayingSo()
    {
        var (service, repo, couple, owner) = Incomes();
        var fromBank = IncomeSource.Create(couple.Id, owner.Id, "2026-10", "Pagamento Empresa Exemplo", 3500m, "BRL", true, Now, isRecurring: false);
        var typed = IncomeSource.Create(couple.Id, owner.Id, "2026-10", "Freela", 100m, "BRL", true, Now, isRecurring: false);
        repo.Sources.AddRange([fromBank, typed]);
        repo.CreatedFromBank.Add(fromBank.Id);

        var month = await service.GetMonthlyIncomeAsync(couple.Id, owner.Id, "2026-10", CancellationToken.None);

        Assert.Equal(
            new[] { ("Pagamento Empresa Exemplo", true), ("Freela", false) },
            month.SharedIncome.Sources.Select(s => (s.Name, s.FromBank)));

        var updated = await service.UpdateAsync(couple.Id, owner.Id, fromBank.Id, new Application.Income.Commands.UpdateIncomeSourceInput("Salário", null, null, null), CancellationToken.None);
        Assert.True(updated.FromBank);
        var updatedTyped = await service.UpdateAsync(couple.Id, owner.Id, typed.Id, new Application.Income.Commands.UpdateIncomeSourceInput("Bico", null, null, null), CancellationToken.None);
        Assert.False(updatedTyped.FromBank);
    }

    [Fact]
    public async Task DeletingAnIncome_ReleasesTheLineOfTheBankItCameFrom_AndARefusedDeleteReleasesNothing()
    {
        var (service, repo, couple, owner) = Incomes();
        var source = IncomeSource.Create(couple.Id, owner.Id, "2026-10", "Pagamento Empresa Exemplo", 3500m, "BRL", false, Now, isRecurring: false);
        repo.Sources.Add(source);

        // Not the owner and not shared: refused, and the line of the bank stays as it is.
        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteAsync(couple.Id, Guid.NewGuid(), source.Id, CancellationToken.None));
        Assert.Empty(repo.Released);
        // Another group: it does not exist.
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(Guid.NewGuid(), owner.Id, source.Id, CancellationToken.None));
        Assert.Empty(repo.Released);

        await service.DeleteAsync(couple.Id, owner.Id, source.Id, CancellationToken.None);

        Assert.Equal(new[] { source.Id }, repo.Released);
        Assert.Empty(repo.Sources);
    }
}
