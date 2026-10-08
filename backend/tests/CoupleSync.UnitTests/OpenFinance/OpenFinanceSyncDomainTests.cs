using CoupleSync.Application.OpenFinance;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;

namespace CoupleSync.UnitTests.OpenFinance;

/// <summary>Issue #25 — the mirror row, the run and the days a run asks Pluggy for. Every value is invented.</summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceSyncDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);

    private static BankTransactionSnapshot Snapshot(
        DateTime? date = null, decimal amount = -58.90m, string? type = "DEBIT", string? status = "POSTED",
        string? description = "Cantina Exemplo", string? currency = "BRL")
        => new(
            date ?? new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc), amount, type, currency, description, "COMPRA CANTINA EXEMPLO",
            "Eating out", "11010000", "Cantina Exemplo Ltda", "99999999999999", "Restaurants", "PIX", 2, 6, "bill-1", status, 100m,
            """{"id":"t-1"}""");

    private static BankTransaction NewRow(BankTransactionSnapshot? snapshot = null)
        => BankTransaction.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " t-1 ", snapshot ?? Snapshot(), "ALIMENTACAO", Guid.NewGuid(), Now);

    [Fact]
    public void ANewRow_WaitsForTheReview_WithEverythingPluggySaid()
    {
        var row = NewRow();

        Assert.Equal("t-1", row.PluggyTransactionId);
        Assert.Equal(BankTransactionReviewState.Pending, row.ReviewState);
        Assert.Equal(BankTransactionType.Debit, row.Type);
        Assert.Equal(BankTransactionStatus.Posted, row.Status);
        Assert.Equal(-58.90m, row.Amount);
        Assert.Equal(58.90m, row.AbsoluteAmount);
        Assert.True(row.IsExpense);
        Assert.Equal("BRL", row.Currency);
        Assert.Equal(new DateOnly(2026, 10, 5), row.LocalDate);
        Assert.Equal("ALIMENTACAO", row.SuggestedCategory);
        Assert.Equal("Cantina Exemplo Ltda", row.MerchantName);
        Assert.Equal((2, 6), (row.InstallmentNumber, row.InstallmentTotal));
        Assert.Equal("""{"id":"t-1"}""", row.RawJson);
        Assert.Null(row.LinkedTransactionId);
        Assert.Null(row.ReviewedAtUtc);
    }

    [Fact]
    public void Refresh_TakesWhatPluggySaysNow_AndKeepsTheReviewTheLinkAndTheSuggestion()
    {
        var row = NewRow(Snapshot(status: "PENDING", description: "Cantina"));
        var transactionId = Guid.NewGuid();
        row.Confirm(transactionId, Now);
        var run = Guid.NewGuid();

        row.Refresh(Snapshot(status: "POSTED", description: "Cantina Exemplo Centro", amount: -60m), run, Now.AddHours(1));

        Assert.Equal(BankTransactionStatus.Posted, row.Status);
        Assert.Equal("Cantina Exemplo Centro", row.Description);
        Assert.Equal(-60m, row.Amount);
        Assert.Equal(run, row.SyncRunId);
        Assert.Equal(Now.AddHours(1), row.UpdatedAtUtc);
        // Never touched by a synchronisation:
        Assert.Equal(BankTransactionReviewState.Confirmed, row.ReviewState);
        Assert.Equal(transactionId, row.LinkedTransactionId);
        Assert.Equal("ALIMENTACAO", row.SuggestedCategory);
        Assert.Equal(Now, row.ReviewedAtUtc);
        Assert.Equal(Now, row.CreatedAtUtc);
    }

    [Fact]
    public void Discard_ClearsTheLink_AndRestore_BringsTheRowBackToTheReview()
    {
        var row = NewRow();
        row.Confirm(Guid.NewGuid(), Now);

        row.Discard(Now.AddMinutes(1));
        Assert.Equal(BankTransactionReviewState.Discarded, row.ReviewState);
        Assert.Null(row.LinkedTransactionId);
        Assert.Equal(Now.AddMinutes(1), row.ReviewedAtUtc);

        row.Restore(Now.AddMinutes(2));
        Assert.Equal(BankTransactionReviewState.Pending, row.ReviewState);
        Assert.Null(row.LinkedTransactionId);
        Assert.Null(row.ReviewedAtUtc);
    }

    [Theory]
    // An instant: the day it falls on in Brasília (UTC-3).
    [InlineData("2026-10-01T01:30:00Z", "2026-09-30")]
    [InlineData("2026-10-01T03:00:00Z", "2026-10-01")]
    [InlineData("2026-10-05T15:00:00Z", "2026-10-05")]
    [InlineData("2026-10-06T02:59:59Z", "2026-10-05")]
    // Midnight UTC is how a bank that gives no time writes the day: the day as written, not the evening before.
    [InlineData("2026-10-05T00:00:00Z", "2026-10-05")]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-01")]
    public void TheLocalDate_IsTheDayInBrazil_AndADateWithoutTimeIsTheDayAsWritten(string date, string expected)
    {
        var utc = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), BankTransaction.LocalDateOf(utc));
    }

    [Fact]
    public void TheInstantOfTheTransactionCreated_FallsOnTheLocalDay()
    {
        // With a time: exactly the instant Pluggy gave.
        var timed = NewRow(Snapshot(date: new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Utc), timed.EventTimestampUtc());
        Assert.Equal("2026-09", BrazilTime.MonthOf(timed.EventTimestampUtc()));

        // Without a time (midnight UTC): noon of that day in Brasília, so the app shows the same day and month.
        var dateOnly = NewRow(Snapshot(date: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 10, 1), dateOnly.LocalDate);
        Assert.Equal(new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc), dateOnly.EventTimestampUtc());
        Assert.Equal("2026-10", BrazilTime.MonthOf(dateOnly.EventTimestampUtc()));
    }

    [Theory]
    [InlineData("DEBIT", -10, BankTransactionType.Debit)]
    [InlineData("debit", 10, BankTransactionType.Debit)] // a purchase on a credit card is positive
    [InlineData("CREDIT", 10, BankTransactionType.Credit)]
    [InlineData("CREDIT", -10, BankTransactionType.Credit)] // a payment of the card
    [InlineData(null, -10, BankTransactionType.Debit)]
    [InlineData("", 10, BankTransactionType.Credit)]
    [InlineData("SOMETHING", -0.01, BankTransactionType.Debit)]
    public void TheType_IsTheOnePluggyGives_AndWithoutOneTheSignDecides(string? type, double amount, BankTransactionType expected)
        => Assert.Equal(expected, BankTransaction.TypeOf(type, (decimal)amount));

    [Fact]
    public void TextsLongerThanTheirColumns_AreCut_AndAnotherCurrencyIsKept()
    {
        var row = NewRow(Snapshot(description: new string('x', 600), currency: " usd "));

        Assert.Equal(BankTransaction.MaxDescriptionLength, row.Description!.Length);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(BankTransactionStatus.Pending, NewRow(Snapshot(status: " pending ")).Status);
        Assert.Equal(BankTransactionStatus.Posted, NewRow(Snapshot(status: null)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ARowWithoutThePluggyId_IsRefused(string id)
        => Assert.Throws<ArgumentException>(() => BankTransaction.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), id, Snapshot(), null, Guid.NewGuid(), Now));

    // ---------------------------------------------------------------- the run

    [Fact]
    public void ARun_GoesFromWaitingToRunningToDone_WithItsCounts()
    {
        var run = SyncRun.Create(Guid.NewGuid(), Guid.NewGuid(), SyncRunTrigger.User, forceItemUpdate: true, aiCategorizationConsent: true, Now);
        Assert.Equal(SyncRunStatus.Pending, run.Status);
        Assert.True(run.IsOpen);
        Assert.True(run.ForceItemUpdate);
        Assert.True(run.AiCategorizationConsent);
        Assert.Null(run.StartedAtUtc);

        run.MarkRunning(Now.AddSeconds(1));
        Assert.Equal(SyncRunStatus.Running, run.Status);
        Assert.True(run.IsOpen);

        run.AddCounts(3, 2);
        run.AddCounts(1, 0);
        run.MarkDone(Now.AddSeconds(9));

        Assert.Equal(SyncRunStatus.Done, run.Status);
        Assert.False(run.IsOpen);
        Assert.Equal((4, 2), (run.TransactionsNew, run.TransactionsUpdated));
        Assert.Equal(Now.AddSeconds(9), run.FinishedAtUtc);
        Assert.Null(run.ErrorCode);
    }

    [Fact]
    public void AFailedRun_KeepsTheCodeAndTheMessage_CutToTheirColumns()
    {
        var run = SyncRun.Create(Guid.NewGuid(), Guid.NewGuid(), SyncRunTrigger.Scheduler, false, false, Now);

        run.MarkFailed(new string('C', 100), new string('m', 900), Now);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.False(run.IsOpen);
        Assert.Equal(SyncRun.MaxErrorCodeLength, run.ErrorCode!.Length);
        Assert.Equal(SyncRun.MaxErrorMessageLength, run.ErrorMessage!.Length);
        Assert.Equal(Now, run.FinishedAtUtc);
    }

    // ---------------------------------------------------------------- the days asked

    private static BankConnection Connection(int historyMonths, DateTime? lastSync = null)
    {
        var connection = BankConnection.Create(Guid.NewGuid(), Guid.NewGuid(), "Bancos", "enc-id", "enc-secret", "0a1b", historyMonths, Now);
        if (lastSync is { } at) connection.MarkSynced(at);
        return connection;
    }

    [Theory]
    [InlineData(3, "2026-07-07")]
    [InlineData(6, "2026-04-07")]
    [InlineData(12, "2025-10-07")]
    public void TheFirstRun_AsksFromTodayMinusTheMonthsChosen(int historyMonths, string expectedFrom)
    {
        var window = SyncWindow.For(Connection(historyMonths), Now);

        Assert.Equal(expectedFrom, window.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(window.From, window.HistoryFrom);
        Assert.Equal(new DateOnly(2026, 10, 7), window.To);
    }

    [Fact]
    public void TheNextRuns_AskFromTheLastSuccessfulSynchronisationMinus7Days()
    {
        var window = SyncWindow.For(Connection(12, lastSync: new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc)), Now);

        Assert.Equal(new DateOnly(2026, 9, 26), window.From);
        Assert.Equal(new DateOnly(2025, 10, 7), window.HistoryFrom);
        Assert.Equal(new DateOnly(2026, 10, 7), window.To);
    }

    [Theory]
    // Never read: the whole history, whatever the connection did before.
    [InlineData(null, "2026-07-07")]
    // Read all along (last transaction after the window of the connection): the window of the connection.
    [InlineData("2026-10-06", "2026-09-26")]
    [InlineData("2026-10-03", "2026-09-26")]
    // Unread for weeks while the connection kept synchronising: its own last transaction minus 7 days.
    [InlineData("2026-09-10", "2026-09-03")]
    // Its last transaction is older than the history: never further back than the history.
    [InlineData("2026-07-09", "2026-07-07")]
    [InlineData("2026-05-01", "2026-07-07")]
    public void EachAccount_IsAskedFromTheEarlierOfTheWindowAndItsOwnLastTransactionMinus7Days_WithinTheHistory(string? lastDayOfAccount, string expectedFrom)
    {
        var window = SyncWindow.For(Connection(3, lastSync: new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc)), Now);
        DateOnly? last = lastDayOfAccount is null ? null : DateOnly.ParseExact(lastDayOfAccount, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expectedFrom, window.FromFor(last).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheDaysAreTheOnesOfBrazil_AndTheLastDayIsTodayInUtc()
    {
        // 01:00 UTC of the 8th is still the 7th in Brasília: the history starts from the 7th; the last day asked is
        // the 8th (UTC, as Pluggy reads it), so a purchase of this evening is already inside.
        var night = new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc);
        var first = SyncWindow.For(Connection(3), night);
        Assert.Equal(new DateOnly(2026, 7, 7), first.From);
        Assert.Equal(new DateOnly(2026, 10, 8), first.To);

        // A last synchronisation at 02:00 UTC of the 4th happened on the 3rd in Brasília.
        var next = SyncWindow.For(Connection(3, lastSync: new DateTime(2026, 10, 4, 2, 0, 0, DateTimeKind.Utc)), night);
        Assert.Equal(new DateOnly(2026, 9, 26), next.From);
    }

    [Fact]
    public void MarkSynced_MovesTheLastSynchronisation_ClearsTheError_AndNeverRevivesADisconnectedConnection()
    {
        var connection = Connection(3);
        connection.MarkError("PLUGGY_INVALID_CREDENTIALS", "recusadas", Now);

        connection.MarkSynced(Now.AddMinutes(5));

        Assert.Equal(BankConnectionStatus.Active, connection.Status);
        Assert.Null(connection.LastErrorCode);
        Assert.Equal(Now.AddMinutes(5), connection.LastSyncAtUtc);

        connection.Disconnect(Now.AddMinutes(6));
        connection.MarkSynced(Now.AddMinutes(7));
        Assert.Equal(BankConnectionStatus.Disconnected, connection.Status);
        Assert.Equal(Now.AddMinutes(5), connection.LastSyncAtUtc);
    }

    [Fact]
    public void ThePeriod_CanBeChangedTo3_6Or12Months_AndNothingElse()
    {
        var connection = Connection(3);

        connection.SetHistoryMonths(12, Now.AddMinutes(1));
        Assert.Equal(12, connection.HistoryMonths);
        Assert.Equal(Now.AddMinutes(1), connection.UpdatedAtUtc);

        Assert.Throws<ArgumentException>(() => connection.SetHistoryMonths(5, Now));
        Assert.Equal(12, connection.HistoryMonths);
    }

    // ---------------------------------------------------------------- the identity of the transaction created

    [Fact]
    public void TheFingerprint_DependsOnlyOnTheGroupAndThePluggyId()
    {
        var generator = new TransactionFingerprintGenerator();
        var couple = Guid.NewGuid();

        var first = BankReviewService.FingerprintOf(generator, couple, "c1b2c3d4-0000-4000-8000-000000000101");

        Assert.Equal(first, BankReviewService.FingerprintOf(generator, couple, "c1b2c3d4-0000-4000-8000-000000000101"));
        Assert.NotEqual(first, BankReviewService.FingerprintOf(generator, couple, "c1b2c3d4-0000-4000-8000-000000000102"));
        Assert.NotEqual(first, BankReviewService.FingerprintOf(generator, Guid.NewGuid(), "c1b2c3d4-0000-4000-8000-000000000101"));
        Assert.Equal(64, first.Length);
        Assert.Equal(
            TransactionFingerprintGenerator.GenerateStatic(couple, "OPENFINANCE", 0m, "BRL", DateTime.UnixEpoch, "c1b2c3d4-0000-4000-8000-000000000101"),
            first);
    }

    [Fact]
    public void TheNewSourceAndBank_HaveTheValuesTheDesignFixed()
    {
        Assert.Equal(3, (int)TransactionSource.OpenFinance);
        Assert.Equal("OPENFINANCE", TransactionEventIngest.OpenFinanceBank);
        Assert.Contains(TransactionEventIngest.OpenFinanceBank, TransactionEventIngest.NonNotificationBanks);
        // The ones that were there stay.
        Assert.Equal((0, 1, 2), ((int)TransactionSource.Manual, (int)TransactionSource.OcrImport, (int)TransactionSource.Notification));
        Assert.Contains("MANUAL", TransactionEventIngest.NonNotificationBanks);
        Assert.Contains("OCR", TransactionEventIngest.NonNotificationBanks);
    }
}
