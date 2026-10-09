using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.OpenFinance;

/// <summary>A line of the mirror that has just arrived (or has just been settled at the bank), and who paid it when the bank says so.</summary>
public sealed record ArrivedBankLine(BankTransaction Row, string? PayerName)
{
    /// <summary>Carries a name of a person: prints none of it.</summary>
    public override string ToString() => nameof(ArrivedBankLine);
}

/// <summary>
/// What a synchronisation decides about a line before anyone looks at it (issue #26), with the rules of
/// <see cref="BankReconciliationRules"/>, in this order (the first that matches decides):
/// <list type="number">
/// <item>a transfer between two accounts of the group: both legs are ignored (also when one of them had arrived
/// before and been ignored as a bill payment);</item>
/// <item>the payment of a credit card bill, by its value (the total of a closed bill of a card of the group, due
/// within ten days: each bill is paid by one debit only) or by its description: ignored;</item>
/// <item>a credit paid by who holds another account of the group: ignored;</item>
/// <item>an expense that looks like a transaction the app already has: it stays waiting, pointing to that one.</item>
/// </list>
/// Only lines <see cref="BankTransaction.OpenToAutomaticRules"/>, in reais and with a value. What is ignored was
/// settled at the bank: a line the bank may still change or drop is never ignored. A purchase on a credit card is
/// never a leg of a transfer. Nothing is saved here: the run saves; when someone reviewed a line meanwhile, that
/// review stays and the run decides again about the others (<see cref="IBankSyncRepository.SaveRunProgressAsync"/>).
/// </summary>
public sealed class BankPreclassificationService
{
    private readonly IBankSyncRepository _sync;
    private readonly IDateTimeProvider _clock;

    public BankPreclassificationService(IBankSyncRepository sync, IDateTimeProvider clock)
    {
        _sync = sync;
        _clock = clock;
    }

    /// <summary>The line of the mirror as the rules see it.</summary>
    public static MoneyMovement FactsOf(BankTransaction row) => new(
        row.AbsoluteAmount,
        row.Currency,
        row.LocalDate,
        row.IsExpense ? MoneyDirection.Out : MoneyDirection.In,
        row.BankAccountId);

    /// <summary>
    /// Decides about <paramref name="lines"/>, all of <paramref name="account"/> (of the bank named
    /// <paramref name="connectorName"/>), looking at the whole group <paramref name="coupleId"/>.
    /// <paramref name="leaving"/> are the lines of the account this same reading takes out of the mirror (the bank no
    /// longer lists them): the transaction one of them was suspected of repeating is free for the line that replaces
    /// it (a pending purchase the bank settled under another id).
    /// </summary>
    public async Task ApplyAsync(
        Guid coupleId,
        BankAccount account,
        string? connectorName,
        IReadOnlyList<ArrivedBankLine> lines,
        IReadOnlyCollection<BankTransaction> leaving,
        CancellationToken ct)
    {
        // Oldest first, so that the same data always pairs the same way.
        var open = lines
            .Where(l => l.Row.OpenToAutomaticRules && BankReconciliationRules.HasComparableValue(FactsOf(l.Row)))
            .OrderBy(l => l.Row.Date)
            .ThenBy(l => l.Row.PluggyTransactionId, StringComparer.Ordinal)
            .ToList();
        if (open.Count == 0) return;

        var firstDay = open.Min(l => l.Row.LocalDate);
        var lastDay = open.Max(l => l.Row.LocalDate);
        var settled = open.Where(l => l.Row.Status == BankTransactionStatus.Posted).ToList();

        // A purchase on a credit card is never a leg: neither the line that arrives nor the one that waits.
        var mayBeLegs = settled.Where(l => BankReconciliationRules.CanBeTransferLeg(FactsOf(l.Row), account.Type)).ToList();
        IReadOnlyList<BankTransaction> otherLegs = [];
        if (mayBeLegs.Count > 0)
        {
            var accountTypes = await _sync.GetAccountTypesForJobAsync(coupleId, ct);
            otherLegs = (await _sync.GetTransferCandidatesForJobAsync(
                    coupleId, mayBeLegs.Min(l => l.Row.LocalDate), mayBeLegs.Max(l => l.Row.LocalDate), ct))
                .Where(c => BankReconciliationRules.CanBeTransferLeg(FactsOf(c), accountTypes.GetValueOrDefault(c.BankAccountId)))
                .ToList();
        }

        IReadOnlyList<string?> holdersOfOtherAccounts =
            settled.Any(l => !l.Row.IsExpense && !string.IsNullOrWhiteSpace(l.PayerName))
                ? (await _sync.GetAccountHoldersForJobAsync(coupleId, ct))
                    .Where(h => h.AccountId != account.Id)
                    .Select(h => h.UserName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [];

        var unpaidBills = await UnpaidBillsForAsync(
            coupleId, settled.Where(l => BankReconciliationRules.CanPayABill(FactsOf(l.Row), account.Type)).Select(l => l.Row.LocalDate).ToList(), ct);

        var (fromUtc, toUtcExclusive) = BankReconciliationRules.DuplicateSearchWindowUtc(firstDay, lastDay);
        var existing = open.Any(l => l.Row.IsExpense)
            ? (await _sync.GetDuplicateCandidatesForJobAsync(
                coupleId,
                fromUtc,
                toUtcExclusive,
                leaving.Where(l => l.MatchedTransactionId is not null).Select(l => l.Id).ToList(),
                ct)).ToList()
            : [];

        var now = _clock.UtcNow;
        foreach (var line in open)
        {
            var row = line.Row;
            var facts = FactsOf(row);

            // What the bank has not settled may still change or vanish: it is never ignored, only (below) suspected.
            if (row.Status == BankTransactionStatus.Posted)
            {
                // A leg already paired is no longer one to pair: each leg pairs with one other only. The other leg may
                // have arrived first and been ignored as a bill payment (the accounts of a bank are read one after
                // the other): the pair is a transfer all the same, whatever the order of arrival.
                var otherLeg = BankReconciliationRules.CanBeTransferLeg(facts, account.Type)
                    ? BankReconciliationRules.FindTransferCounterpart(facts, otherLegs.Where(c => c.CanBePairedAsTransfer), FactsOf)
                    : null;
                if (otherLeg is not null)
                {
                    row.PairAsTransfer(now);
                    otherLeg.PairAsTransfer(now);
                    // Money that left for another account of the group, of exactly the total of a bill, counts as its
                    // payment (it is what paying the card looks like): no other debit is ignored for that bill.
                    unpaidBills.TakeBillPaidBy(facts);
                    continue;
                }

                // The bill is taken (paid) whichever of the two halves of the rule says so: one bill, one payment.
                // There are bills to take only when this account can pay one (a purchase on a card never does).
                var paysABillByValue = unpaidBills.TakeBillPaidBy(facts);
                if (paysABillByValue || BankReconciliationRules.IsBillPayment(facts, account.Type, row.DescriptionRaw ?? row.Description))
                {
                    row.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, now);
                    continue;
                }

                if (BankReconciliationRules.IsCreditFromGroupHolder(facts, line.PayerName, holdersOfOtherAccounts))
                {
                    row.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, now);
                    continue;
                }
            }

            var duplicate = BankReconciliationRules.PickSuspectedDuplicate(facts, connectorName, existing);
            if (duplicate is null) continue;
            row.MarkSuspectedDuplicate(duplicate.Id, now);
            existing.Remove(duplicate); // one transaction of the app is suspected by one line only
        }
    }

    /// <summary>
    /// The bills of the cards of the group changed (they have just been read, <paramref name="changedDueDates"/> are
    /// their due dates): the debits already in the mirror, of any account of the group, that nobody reviewed and
    /// that pay one of the bills due around those days are ignored as bill payments. This is what recognises the
    /// payment when the debit arrived before the bill (the checking account is read before the card; the bill closes
    /// after the first synchronisation). Reads what is stored: call it after the bills were saved. Nothing is saved here.
    /// </summary>
    public async Task ApplyBillPaymentsAsync(Guid coupleId, IReadOnlyCollection<DateOnly> changedDueDates, CancellationToken ct)
    {
        if (changedDueDates.Count == 0) return;

        var tolerance = BankReconciliationRules.BillPaymentDayTolerance;
        var firstDue = changedDueDates.Min();
        var lastDue = changedDueDates.Max();
        var bills = (await _sync.GetBillFactsForJobAsync(coupleId, firstDue, lastDue, ct)).Where(BankReconciliationRules.IsPayableBill).ToList();
        if (bills.Count == 0) return;

        var lines = await _sync.GetBillPaymentCandidatesForJobAsync(coupleId, firstDue.AddDays(-tolerance), lastDue.AddDays(tolerance), ct);
        var unpaidBills = new UnpaidBills(bills, lines.Where(l => l.ReviewState == BankTransactionReviewState.Ignored).Select(FactsOf));
        var waiting = lines.Where(l => l.OpenToAutomaticRules).ToList();
        if (waiting.Count == 0) return;

        var accountTypes = await _sync.GetAccountTypesForJobAsync(coupleId, ct);
        var now = _clock.UtcNow;
        foreach (var line in waiting)
        {
            var facts = FactsOf(line);
            if (BankReconciliationRules.CanPayABill(facts, accountTypes.GetValueOrDefault(line.BankAccountId)) && unpaidBills.TakeBillPaidBy(facts))
                line.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, now);
        }
    }

    /// <summary>
    /// The bills of the group a debit of one of <paramref name="payingDays"/> can still be the payment of: the closed
    /// ones due around those days, less the ones a line already ignored (a payment recognised before, or the money
    /// sent to the card) has paid.
    /// </summary>
    private async Task<UnpaidBills> UnpaidBillsForAsync(Guid coupleId, IReadOnlyCollection<DateOnly> payingDays, CancellationToken ct)
    {
        if (payingDays.Count == 0) return UnpaidBills.None;

        var tolerance = BankReconciliationRules.BillPaymentDayTolerance;
        var firstDay = payingDays.Min();
        var lastDay = payingDays.Max();
        var bills = (await _sync.GetBillFactsForJobAsync(coupleId, firstDay.AddDays(-tolerance), lastDay.AddDays(tolerance), ct))
            .Where(BankReconciliationRules.IsPayableBill)
            .ToList();
        if (bills.Count == 0) return UnpaidBills.None;

        // A line that paid one of those bills is at most the tolerance away from its due date.
        var before = await _sync.GetBillPaymentCandidatesForJobAsync(coupleId, firstDay.AddDays(-2 * tolerance), lastDay.AddDays(2 * tolerance), ct);
        return new UnpaidBills(bills, before.Where(l => l.ReviewState == BankTransactionReviewState.Ignored).Select(FactsOf));
    }

    /// <summary>The bills nobody paid yet, as far as the mirror knows: each one is taken by one debit only.</summary>
    private sealed class UnpaidBills
    {
        public static UnpaidBills None => new([], []);

        private readonly List<CardBillFacts> _bills;

        public UnpaidBills(IEnumerable<CardBillFacts> bills, IEnumerable<MoneyMovement> alreadyPaid)
        {
            _bills = bills.ToList();
            foreach (var payment in alreadyPaid) TakeBillPaidBy(payment);
        }

        /// <summary>True, taking the bill, when <paramref name="movement"/> is the payment of one still unpaid.</summary>
        public bool TakeBillPaidBy(MoneyMovement movement)
        {
            var bill = BankReconciliationRules.FindBillPaid(movement, _bills);
            return bill is not null && _bills.Remove(bill);
        }
    }
}
