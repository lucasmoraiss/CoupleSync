using CoupleSync.Application.OpenFinance;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.OpenFinance;

/// <summary>
/// Issue #26 — the rules of the reconciliation, as pure functions (no Pluggy, no database), and what the mirror row
/// and the transaction do with a decision. Every name and value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class BankReconciliationRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 3);
    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid AccountB = Guid.NewGuid();

    private static MoneyMovement Out(decimal amount, DateOnly? day = null, Guid? account = null, string? currency = "BRL")
        => new(amount, currency, day ?? Day, MoneyDirection.Out, account ?? AccountA);

    private static MoneyMovement In(decimal amount, DateOnly? day = null, Guid? account = null, string? currency = "BRL")
        => new(amount, currency, day ?? Day, MoneyDirection.In, account ?? AccountB);

    private static ExistingTransactionFacts Existing(
        decimal amount = 25.00m, DateOnly? day = null, TransactionSource source = TransactionSource.Manual, string? bank = "MANUAL",
        string? currency = "BRL", Guid? id = null)
        => new(id ?? Guid.NewGuid(), amount, currency, day ?? Day, source, bank);

    // ---------------------------------------------------------------- value

    [Theory]
    [InlineData(25.00, "BRL", true)]
    [InlineData(25.00, "brl", true)]
    [InlineData(0, "BRL", false)]
    [InlineData(-25.00, "BRL", false)]
    [InlineData(25.00, "USD", false)]
    [InlineData(25.00, null, false)]
    [InlineData(25.00, "", false)]
    public void OnlyAValueInReaisAndGreaterThanZero_CanBeCompared(double amount, string? currency, bool expected)
        => Assert.Equal(expected, BankReconciliationRules.HasComparableValue(new MoneyMovement((decimal)amount, currency, Day, MoneyDirection.Out)));

    // ---------------------------------------------------------------- rule 1: own transfer

    [Fact]
    public void SameValue_OppositeWays_SameDay_TwoAccounts_IsATransfer_InBothOrders()
    {
        Assert.True(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), In(500.00m)));
        Assert.True(BankReconciliationRules.IsOwnTransferPair(In(500.00m), Out(500.00m)));
    }

    [Fact]
    public void ATransfer_NeedsEveryOneOfItsConditions()
    {
        // another value (one cent)
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), In(500.01m)));
        // the same way twice
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), Out(500.00m, account: AccountB)));
        Assert.False(BankReconciliationRules.IsOwnTransferPair(In(500.00m, account: AccountA), In(500.00m)));
        // another day
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), In(500.00m, day: Day.AddDays(1))));
        // the same account
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), In(500.00m, account: AccountA)));
        // an account nobody knows (a source that has none)
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m) with { AccountId = null }, In(500.00m)));
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), In(500.00m) with { AccountId = null }));
    }

    [Fact]
    public void ATransfer_IsOnlyInReais_AndNeverOfZero()
    {
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m, currency: "USD"), In(500.00m, currency: "USD")));
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m), In(500.00m, currency: "USD")));
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(500.00m, currency: "EUR"), In(500.00m)));
        Assert.False(BankReconciliationRules.IsOwnTransferPair(Out(0m), In(0m)));
    }

    [Fact]
    public void TheCounterpart_IsTheFirstCandidateThatMatches_AndNoneWhenNoneDoes()
    {
        var wrongDay = In(500.00m, day: Day.AddDays(-1));
        var first = In(500.00m);
        var second = In(500.00m);
        var candidates = new[] { wrongDay, first, second };

        Assert.Same(first, BankReconciliationRules.FindTransferCounterpart(Out(500.00m), candidates, c => c));
        Assert.Null(BankReconciliationRules.FindTransferCounterpart(Out(499.00m), candidates, c => c));
        Assert.Null(BankReconciliationRules.FindTransferCounterpart(Out(500.00m), Array.Empty<MoneyMovement>(), c => c));
    }

    [Theory]
    [InlineData("CREDIT")]
    [InlineData("credit")]
    [InlineData(" Credit ")]
    public void MoneyThatLeftACreditCard_IsAPurchase_NeverALegOfATransfer(string accountType)
    {
        Assert.False(BankReconciliationRules.CanBeTransferLeg(Out(100.00m), accountType));
        // Money that enters the card is the payment of the bill: the other leg of what left a checking account.
        Assert.True(BankReconciliationRules.CanBeTransferLeg(In(100.00m), accountType));
    }

    [Theory]
    [InlineData("BANK")]
    [InlineData("INVESTMENT")]
    [InlineData("")]
    [InlineData(null)]
    public void InAnyOtherAccount_BothWaysCanBeALegOfATransfer(string? accountType)
    {
        Assert.True(BankReconciliationRules.CanBeTransferLeg(Out(100.00m), accountType));
        Assert.True(BankReconciliationRules.CanBeTransferLeg(In(100.00m), accountType));
    }

    // ---------------------------------------------------------------- rule 2: bill payment

    [Theory]
    [InlineData("PAGAMENTO DE FATURA", true)]
    [InlineData("Pagamento de fatura", true)]
    [InlineData("pagamento fatura cartão exemplo", true)]
    [InlineData("FATURA - PAGAMENTO EFETUADO", true)]
    [InlineData("PÁGAMENTO DA FÁTURA", true)]
    [InlineData("PAGAMENTO DE BOLETO", false)]
    [InlineData("FATURA DE ENERGIA", false)]
    [InlineData("PAGTO FATURA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABillPayment_IsADescriptionWithPagamentoAndFatura_WhateverTheCaseAndTheAccents(string? description, bool expected)
        => Assert.Equal(expected, BankReconciliationRules.IsBillPaymentDescription(description));

    [Fact]
    public void ABillPayment_IsMoneyLeavingACheckingAccount_InReais()
    {
        const string text = "PAGAMENTO DE FATURA";
        Assert.True(BankReconciliationRules.IsBillPayment(Out(1200.00m), "BANK", text));
        Assert.True(BankReconciliationRules.IsBillPayment(Out(1200.00m), "bank", text));

        // on the card itself the line is the credit of the payment, or a purchase: never this rule
        Assert.False(BankReconciliationRules.IsBillPayment(Out(1200.00m), "CREDIT", text));
        Assert.False(BankReconciliationRules.IsBillPayment(Out(1200.00m), null, text));
        Assert.False(BankReconciliationRules.IsBillPayment(In(1200.00m), "BANK", text));
        Assert.False(BankReconciliationRules.IsBillPayment(Out(1200.00m, currency: "USD"), "BANK", text));
        Assert.False(BankReconciliationRules.IsBillPayment(Out(0m), "BANK", text));
        Assert.False(BankReconciliationRules.IsBillPayment(Out(1200.00m), "BANK", "COMPRA MERCADO EXEMPLO"));
    }

    // ---------------------------------------------------------------- rule 3: credit from someone of the group

    [Theory]
    [InlineData("BRUNO EXEMPLO SILVA", "Bruno Silva", true)]
    [InlineData("Bruno Silva", "Bruno Silva", true)]
    [InlineData("JOÃO DA CONCEIÇÃO", "joao conceicao", true)]
    [InlineData("BRUNO EXEMPLO SILVA", "Bruno", false)] // a first name alone says too little
    [InlineData("BRUNO EXEMPLO", "Bruno Silva", false)]
    [InlineData("CARLA EXEMPLO SILVA", "Bruno Silva", false)]
    [InlineData("", "Bruno Silva", false)]
    [InlineData(null, "Bruno Silva", false)]
    [InlineData("BRUNO EXEMPLO SILVA", null, false)]
    [InlineData("BRUNO EXEMPLO SILVA", "  ", false)]
    public void TwoNames_AreTheSamePerson_WhenEveryWordOfTheHolderIsInThePayer(string? payer, string? holder, bool expected)
        => Assert.Equal(expected, BankReconciliationRules.IsSamePerson(payer, holder));

    [Fact]
    public void ACreditFromAHolderOfAnotherAccountOfTheGroup_IsMoneyOfTheGroup()
    {
        var holders = new[] { "Carla Modelo", null, "Bruno Silva" };

        Assert.True(BankReconciliationRules.IsCreditFromGroupHolder(In(300.00m), "BRUNO EXEMPLO SILVA", holders));

        Assert.False(BankReconciliationRules.IsCreditFromGroupHolder(In(300.00m), "EMPRESA EXEMPLO LTDA", holders));
        Assert.False(BankReconciliationRules.IsCreditFromGroupHolder(In(300.00m), null, holders));
        Assert.False(BankReconciliationRules.IsCreditFromGroupHolder(In(300.00m), "BRUNO EXEMPLO SILVA", Array.Empty<string?>()));
        // money that left is never a credit
        Assert.False(BankReconciliationRules.IsCreditFromGroupHolder(Out(300.00m), "BRUNO EXEMPLO SILVA", holders));
        Assert.False(BankReconciliationRules.IsCreditFromGroupHolder(In(300.00m, currency: "USD"), "BRUNO EXEMPLO SILVA", holders));
        Assert.False(BankReconciliationRules.IsCreditFromGroupHolder(In(0m), "BRUNO EXEMPLO SILVA", holders));
    }

    // ---------------------------------------------------------------- rule 4: suspected duplicate

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(2, false)]
    [InlineData(-2, false)]
    [InlineData(3, false)]
    public void ADuplicate_IsTheSameValue_OnTheSameDayOrOneDayApart(int daysApart, bool expected)
        => Assert.Equal(
            expected,
            BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m, day: Day.AddDays(daysApart)), "Banco Exemplo", Existing()));

    [Fact]
    public void ADuplicate_NeedsTheSameValueToTheCent_InReais_OnBothSides()
    {
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(Out(25.01m), "Banco Exemplo", Existing()));
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m, currency: "USD"), "Banco Exemplo", Existing()));
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m), "Banco Exemplo", Existing(currency: "USD")));
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m, currency: "USD"), "Banco Exemplo", Existing(currency: "USD")));
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(Out(0m), "Banco Exemplo", Existing(amount: 0m)));
    }

    [Fact]
    public void ADuplicate_IsOnlyMoneyThatLeft_AndNeverATransactionThatAlreadyCameFromTheBank()
    {
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(In(25.00m), "Banco Exemplo", Existing()));
        Assert.False(BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m), "Banco Exemplo", Existing(source: TransactionSource.OpenFinance, bank: "Banco Exemplo")));

        Assert.True(BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m), "Banco Exemplo", Existing(source: TransactionSource.OcrImport, bank: "OCR")));
        Assert.True(BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m), "Nubank", Existing(source: TransactionSource.Notification, bank: "NUBANK")));
    }

    [Theory]
    [InlineData("NUBANK", "Nubank", true)]
    [InlineData("NUBANK", "Nu Pagamentos S.A.", true)]
    [InlineData("NUBANK", "Itaú", false)]
    [InlineData("NUBANK", "Banco do Brasil", false)]
    [InlineData("ITAU", "Itaú", true)]
    [InlineData("ITAU", "Itaú Cartões", true)]
    [InlineData("ITAU", "Nubank", false)]
    [InlineData("INTER", "Banco Inter", true)]
    [InlineData("C6", "C6 Bank", true)]
    [InlineData("BB", "Banco do Brasil", true)]
    [InlineData("BB", "Bradesco", false)]
    [InlineData("CAIXA", "Caixa Econômica Federal", true)]
    // The name of the connection is not one of the banks the app knows (every connection made through "Meu Pluggy"
    // may be named after it, not after the bank): nothing to compare with, the suspicion stays.
    [InlineData("NUBANK", "MeuPluggy", true)]
    [InlineData("NUBANK", "Meu Pluggy", true)]
    [InlineData("NUBANK", "Banco Exemplo", true)]
    [InlineData("INTER", "Banco Internacional Exemplo", true)]
    [InlineData("SANTANDER", null, true)]
    [InlineData("SANTANDER", "", true)]
    [InlineData("SANTANDER", "   ", true)]
    [InlineData("MANUAL", "Nubank", true)]   // not a bank the app knows: the bank is not looked at
    [InlineData("OCR", "Nubank", true)]
    [InlineData("OCR Import", "Itaú", true)]
    [InlineData(null, "Nubank", true)]
    [InlineData("", "Nubank", true)]
    public void TheBankDiscardsTheSuspicion_OnlyWhenBothAreBanksTheAppKnows_AndTheyAreNotTheSame(string? transactionBank, string? connector, bool expected)
    {
        Assert.Equal(expected, BankReconciliationRules.IsSameBankOrUnknown(transactionBank, connector));
        Assert.Equal(
            expected,
            BankReconciliationRules.IsSuspectedDuplicate(Out(25.00m), connector, Existing(source: TransactionSource.Notification, bank: transactionBank)));
    }

    [Theory]
    [InlineData("NUBANK", true)]
    [InlineData("nubank", true)]
    [InlineData("Itaú", true)]
    [InlineData("MANUAL", false)]
    [InlineData("OCR", false)]
    [InlineData("OPENFINANCE", false)]
    [InlineData(null, false)]
    public void TheKnownBanks_AreTheOnesTheNotificationCaptureNames(string? bank, bool expected)
        => Assert.Equal(expected, BankReconciliationRules.IsKnownBank(bank));

    [Fact]
    public void AmongSeveralCandidates_TheSameDayWins_ThenTheSmallestId_AndWhatDoesNotMatchIsNeverPicked()
    {
        var dayBefore = Existing(day: Day.AddDays(-1), id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var sameDayB = Existing(id: Guid.Parse("00000000-0000-0000-0000-0000000000bb"));
        var sameDayA = Existing(id: Guid.Parse("00000000-0000-0000-0000-0000000000aa"));
        var otherValue = Existing(amount: 26.00m);

        Assert.Equal(sameDayA, BankReconciliationRules.PickSuspectedDuplicate(Out(25.00m), "Banco Exemplo", [otherValue, dayBefore, sameDayB, sameDayA]));
        Assert.Equal(dayBefore, BankReconciliationRules.PickSuspectedDuplicate(Out(25.00m), "Banco Exemplo", [otherValue, dayBefore]));
        Assert.Null(BankReconciliationRules.PickSuspectedDuplicate(Out(25.00m), "Banco Exemplo", [otherValue]));
        Assert.Null(BankReconciliationRules.PickSuspectedDuplicate(Out(25.00m), "Banco Exemplo", []));
    }

    [Fact]
    public void TheDayOfATransactionOfTheApp_IsTheDayInBrazil()
    {
        // 22:00 of the 3rd in Brasília is already the 4th in UTC.
        foreach (var source in new[] { TransactionSource.Manual, TransactionSource.Notification, TransactionSource.OcrImport })
        {
            Assert.Equal(new DateOnly(2026, 10, 3), BankReconciliationRules.LocalDayOf(new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc), source));
            Assert.Equal(new DateOnly(2026, 10, 4), BankReconciliationRules.LocalDayOf(new DateTime(2026, 10, 4, 3, 0, 0, DateTimeKind.Utc), source));
        }
    }

    [Fact]
    public void TheDayOfAnImportedStatementLine_StoredAsMidnightUtc_IsTheDayAsWritten_NotTheEveningBeforeInBrasilia()
    {
        // The statement import stores the date of the statement with no time: "2026-10-03" becomes 2026-10-03T00:00:00Z.
        var written = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 10, 3), BankReconciliationRules.LocalDayOf(written, TransactionSource.OcrImport));
        Assert.Equal(new DateOnly(2026, 10, 3), BankReconciliationRules.LocalDayOf(DateTime.SpecifyKind(written, DateTimeKind.Unspecified), TransactionSource.OcrImport));
        // One tick past midnight is an instant, not a date without a time.
        Assert.Equal(new DateOnly(2026, 10, 2), BankReconciliationRules.LocalDayOf(written.AddTicks(1), TransactionSource.OcrImport));
    }

    [Theory]
    [InlineData(TransactionSource.Manual)]
    [InlineData(TransactionSource.Notification)]
    public void WhatThePersonTypedOrTheNotificationCaptured_IsAlwaysAnInstant_21hInBrasiliaIsNotTheNextDay(TransactionSource source)
    {
        // The app sends 21:00 of the 2nd in Brasília as 2026-10-03T00:00:00.000Z: it is still the 2nd.
        Assert.Equal(
            new DateOnly(2026, 10, 2),
            BankReconciliationRules.LocalDayOf(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), source));
    }

    [Theory]
    [InlineData(3, true)]  // the statement says the 3rd, the bank the 4th: one day apart
    [InlineData(4, true)]  // the same day
    [InlineData(5, true)]  // the day after
    [InlineData(2, false)] // two days before
    [InlineData(6, false)] // two days after
    public void AStatementLineImportedWithNoTime_IsComparedByTheDayWritten(int dayOfTheStatement, bool expected)
    {
        var bankLine = Out(25.00m, day: new DateOnly(2026, 10, 4));
        var imported = new ExistingTransactionFacts(
            Guid.NewGuid(), 25.00m, "BRL",
            BankReconciliationRules.LocalDayOf(new DateTime(2026, 10, dayOfTheStatement, 0, 0, 0, DateTimeKind.Utc), TransactionSource.OcrImport),
            TransactionSource.OcrImport, "OCR Import");

        Assert.Equal(expected, BankReconciliationRules.IsSuspectedDuplicate(bankLine, "Banco Exemplo", imported));
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(3, 3)]
    [InlineData(31, 31)]
    public void TheSearchForCandidates_StartsAtMidnightUtcOfTheDayBeforeTheFirstLine_AndEndsWithTheDayAfterTheLastInBrazil(int firstDay, int lastDay)
    {
        var first = new DateOnly(2026, 10, firstDay);
        var last = new DateOnly(2026, 10, lastDay);

        var (fromUtc, toUtcExclusive) = BankReconciliationRules.DuplicateSearchWindowUtc(first, last);

        // A statement line of the day before is stored at 00:00Z of that day: three hours before that day starts in Brazil.
        Assert.Equal(DateTime.SpecifyKind(first.AddDays(-1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc), fromUtc);
        Assert.Equal(DateTimeKind.Utc, fromUtc.Kind);
        // 23:59 of the day after in Brasília is 02:59Z of the day after that.
        Assert.Equal(DateTime.SpecifyKind(last.AddDays(2).ToDateTime(new TimeOnly(3, 0)), DateTimeKind.Utc), toUtcExclusive);
        Assert.Equal(DateTimeKind.Utc, toUtcExclusive.Kind);
    }

    // ---------------------------------------------------------------- the mirror row

    private static BankTransaction NewRow(decimal amount = -25.00m, string type = "DEBIT")
        => BankTransaction.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "t-26",
            new BankTransactionSnapshot(
                new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc), amount, type, "BRL", "Padaria Exemplo", "COMPRA PADARIA EXEMPLO",
                null, null, null, null, null, null, null, null, null, "POSTED", null, "{}"),
            "ALIMENTACAO", Guid.NewGuid(), Now);

    [Fact]
    public void ForgettingAnAutomaticDecision_PutsTheLineBackAsItArrived_AndNeverTouchesWhatAPersonDecided()
    {
        var ignored = NewRow();
        ignored.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, Now);
        ignored.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Pending, null, null, null), (ignored.ReviewState, ignored.AutoReason, ignored.MatchedTransactionId, ignored.ReviewedAtUtc));
        Assert.True(ignored.OpenToAutomaticRules);

        var paired = NewRow();
        paired.PairAsTransfer(Now);
        paired.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Pending, null, null), (paired.ReviewState, paired.AutoReason, paired.ReviewedAtUtc));

        var suspected = NewRow();
        suspected.MarkSuspectedDuplicate(Guid.NewGuid(), Now);
        suspected.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Pending, null), (suspected.ReviewState, suspected.MatchedTransactionId));

        // What a person decided stays exactly as it is.
        var restored = NewRow();
        restored.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, Now);
        restored.Restore(Now);
        restored.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Pending, Now), (restored.ReviewState, restored.PersonDecidedAtUtc));

        var transaction = Guid.NewGuid();
        var confirmed = NewRow();
        confirmed.Confirm(transaction, Now);
        confirmed.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Confirmed, transaction, Now), (confirmed.ReviewState, confirmed.LinkedTransactionId, confirmed.ReviewedAtUtc));

        var reconciled = NewRow();
        reconciled.Reconcile(transaction, Now);
        reconciled.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Reconciled, transaction, Now), (reconciled.ReviewState, reconciled.LinkedTransactionId, reconciled.ReviewedAtUtc));

        var discarded = NewRow();
        discarded.Discard(Now);
        discarded.ForgetAutomaticDecision();
        Assert.Equal((BankTransactionReviewState.Discarded, Now), (discarded.ReviewState, discarded.ReviewedAtUtc));
    }

    [Fact]
    public void OnlyWhatThePersonDiscarded_CountsAsDiscarded_WhileALineStillHasItsTransaction()
    {
        var waiting = NewRow();
        Assert.False(waiting.CountsAsDiscarded);

        var discarded = NewRow();
        discarded.Discard(Now);
        Assert.True(discarded.CountsAsDiscarded);
        Assert.False(discarded.LostItsTransaction);

        // Reconciled and still pointing to its transaction: neither lost nor discarded.
        var reconciled = NewRow();
        reconciled.Reconcile(Guid.NewGuid(), Now);
        Assert.False(reconciled.LostItsTransaction);
        Assert.False(reconciled.CountsAsDiscarded);

        var confirmed = NewRow();
        confirmed.Confirm(Guid.NewGuid(), Now);
        Assert.False(confirmed.CountsAsDiscarded);

        var ignored = NewRow();
        ignored.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, Now);
        Assert.False(ignored.CountsAsDiscarded);
    }

    [Fact]
    public void IgnoredAutomatically_TheRowKeepsTheReason_AndRestoredItIsThePersonsForGood()
    {
        var row = NewRow();
        Assert.True(row.OpenToAutomaticRules);

        Assert.True(row.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, Now));

        Assert.Equal(BankTransactionReviewState.Ignored, row.ReviewState);
        Assert.Equal("Transfer", row.AutoReason);
        Assert.Equal(Now, row.ReviewedAtUtc);
        Assert.Null(row.PersonDecidedAtUtc);
        Assert.False(row.OpenToAutomaticRules);

        var later = Now.AddHours(1);
        row.Restore(later);

        Assert.Equal(BankTransactionReviewState.Pending, row.ReviewState);
        Assert.Null(row.AutoReason);
        Assert.Null(row.ReviewedAtUtc);
        Assert.Equal(later, row.PersonDecidedAtUtc);
        Assert.False(row.OpenToAutomaticRules);

        // No rule touches it again.
        Assert.False(row.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, later));
        Assert.False(row.MarkSuspectedDuplicate(Guid.NewGuid(), later));
        Assert.Equal(BankTransactionReviewState.Pending, row.ReviewState);
        Assert.Null(row.AutoReason);
        Assert.Null(row.MatchedTransactionId);
    }

    [Fact]
    public void ASuspectedDuplicate_StaysWaiting_AndEveryAnswerOfThePersonClearsTheSuspicion()
    {
        var existing = Guid.NewGuid();

        var same = NewRow();
        Assert.True(same.MarkSuspectedDuplicate(existing, Now));
        Assert.Equal(BankTransactionReviewState.Pending, same.ReviewState);
        Assert.Equal(existing, same.MatchedTransactionId);
        same.Reconcile(existing, Now);
        Assert.Equal(BankTransactionReviewState.Reconciled, same.ReviewState);
        Assert.Equal(existing, same.LinkedTransactionId);
        Assert.Null(same.MatchedTransactionId);
        Assert.Equal(Now, same.ReviewedAtUtc);

        var other = NewRow();
        other.MarkSuspectedDuplicate(existing, Now);
        var created = Guid.NewGuid();
        other.Confirm(created, Now);
        Assert.Equal(BankTransactionReviewState.Confirmed, other.ReviewState);
        Assert.Equal(created, other.LinkedTransactionId);
        Assert.Null(other.MatchedTransactionId);

        var discarded = NewRow();
        discarded.MarkSuspectedDuplicate(existing, Now);
        discarded.Discard(Now);
        Assert.Null(discarded.MatchedTransactionId);
        discarded.Restore(Now);
        Assert.Null(discarded.MatchedTransactionId);
        Assert.False(discarded.OpenToAutomaticRules);
    }

    [Theory]
    [InlineData(BankTransactionReviewState.Confirmed)]
    [InlineData(BankTransactionReviewState.Reconciled)]
    [InlineData(BankTransactionReviewState.Discarded)]
    [InlineData(BankTransactionReviewState.Ignored)]
    public void ARowSomeoneOrSomethingAlreadyDecided_IsNotOpenToTheRules(BankTransactionReviewState state)
    {
        var row = NewRow();
        switch (state)
        {
            case BankTransactionReviewState.Confirmed: row.Confirm(Guid.NewGuid(), Now); break;
            case BankTransactionReviewState.Reconciled: row.Reconcile(Guid.NewGuid(), Now); break;
            case BankTransactionReviewState.Discarded: row.Discard(Now); break;
            default: row.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, Now); break;
        }

        var reason = row.AutoReason;
        Assert.False(row.OpenToAutomaticRules);
        Assert.False(row.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, Now));
        Assert.False(row.MarkSuspectedDuplicate(Guid.NewGuid(), Now));
        Assert.Equal(state, row.ReviewState);
        Assert.Equal(reason, row.AutoReason);
        Assert.Null(row.MatchedTransactionId);
    }

    [Fact]
    public void ALineIgnoredAsBillPayment_BecomesALegOfATransferWhenItsOtherLegArrives_AndNothingElseCanBePaired()
    {
        var waiting = NewRow();
        Assert.True(waiting.CanBePairedAsTransfer);
        Assert.True(waiting.PairAsTransfer(Now));
        Assert.Equal((BankTransactionReviewState.Ignored, "Transfer"), (waiting.ReviewState, waiting.AutoReason));
        // Paired once: not a leg for anything else.
        Assert.False(waiting.CanBePairedAsTransfer);
        Assert.False(waiting.PairAsTransfer(Now));

        var bill = NewRow();
        bill.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, Now);
        Assert.True(bill.CanBePairedAsTransfer);
        var later = Now.AddHours(1);
        Assert.True(bill.PairAsTransfer(later));
        Assert.Equal((BankTransactionReviewState.Ignored, "Transfer", later), (bill.ReviewState, bill.AutoReason, bill.ReviewedAtUtc));

        var restored = NewRow();
        restored.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, Now);
        restored.Restore(Now);
        Assert.False(restored.CanBePairedAsTransfer);
        Assert.False(restored.PairAsTransfer(Now));
        Assert.Equal(BankTransactionReviewState.Pending, restored.ReviewState);

        foreach (var decide in new Action<BankTransaction>[] { r => r.Confirm(Guid.NewGuid(), Now), r => r.Reconcile(Guid.NewGuid(), Now), r => r.Discard(Now) })
        {
            var decided = NewRow();
            decide(decided);
            var state = decided.ReviewState;
            Assert.False(decided.CanBePairedAsTransfer);
            Assert.False(decided.PairAsTransfer(Now));
            Assert.Equal(state, decided.ReviewState);
            Assert.Null(decided.AutoReason);
        }
    }

    // ---------------------------------------------------------------- the existing transaction

    private static Transaction ManualTransaction(string? merchant, string category)
        => Transaction.Create(
            Guid.NewGuid(), Guid.NewGuid(), "fingerprint-26", "MANUAL", 25.00m, CurrencyRules.Brl,
            new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc), "Padaria", merchant, category, Guid.NewGuid(), Now);

    [Theory]
    [InlineData(null, "OUTROS", "Padaria Exemplo Ltda", "ALIMENTACAO")]
    [InlineData("", "OUTROS", "Padaria Exemplo Ltda", "ALIMENTACAO")]
    [InlineData("  ", "Outros", "Padaria Exemplo Ltda", "ALIMENTACAO")]
    [InlineData(null, "", "Padaria Exemplo Ltda", "ALIMENTACAO")]
    public void WhatTheTransactionDoesNotSay_ComesFromTheBank(string? merchant, string category, string expectedMerchant, string expectedCategory)
    {
        var transaction = ManualTransaction(merchant, category);

        Assert.True(transaction.FillMissing("  Padaria Exemplo Ltda ", "Alimentação"));

        Assert.Equal(expectedMerchant, transaction.Merchant);
        Assert.Equal(expectedCategory, transaction.Category);
    }

    [Fact]
    public void WhatThePersonWrote_Stays()
    {
        var transaction = ManualTransaction("Padoca da esquina", "LAZER");

        Assert.False(transaction.FillMissing("Padaria Exemplo Ltda", "ALIMENTACAO"));

        Assert.Equal("Padoca da esquina", transaction.Merchant);
        Assert.Equal("LAZER", transaction.Category);
    }

    [Fact]
    public void NothingFromTheBank_OrOnlyOutros_ChangesNothing_AndEachFieldIsFilledOnItsOwn()
    {
        var empty = ManualTransaction(null, "OUTROS");
        Assert.False(empty.FillMissing(null, null));
        Assert.False(empty.FillMissing("  ", "OUTROS"));
        Assert.False(empty.FillMissing(null, "categoria que não existe"));
        Assert.Null(empty.Merchant);
        Assert.Equal("OUTROS", empty.Category);

        var onlyMerchantMissing = ManualTransaction(null, "LAZER");
        Assert.True(onlyMerchantMissing.FillMissing("Padaria Exemplo Ltda", "ALIMENTACAO"));
        Assert.Equal("Padaria Exemplo Ltda", onlyMerchantMissing.Merchant);
        Assert.Equal("LAZER", onlyMerchantMissing.Category);

        var onlyCategoryMissing = ManualTransaction("Padoca", "OUTROS");
        Assert.True(onlyCategoryMissing.FillMissing("Padaria Exemplo Ltda", "ALIMENTACAO"));
        Assert.Equal("Padoca", onlyCategoryMissing.Merchant);
        Assert.Equal("ALIMENTACAO", onlyCategoryMissing.Category);

        var longName = ManualTransaction(null, "LAZER");
        longName.FillMissing(new string('x', 600), null);
        Assert.Equal(512, longName.Merchant!.Length);
    }
}
