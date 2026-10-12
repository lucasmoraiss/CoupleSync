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
/// <item>an expense that looks like a transaction the app already has: it stays waiting, pointing to that one
/// (<see cref="BankSuspicionJudge"/>: every line that may be a duplicate is marked, whatever the other lines; a
/// line judged again keeps the transaction it pointed to, the same day wins over the day next to it).</item>
/// </list>
/// Rules 1 to 3 decide only when a line arrives or is settled at the bank. Rule 4 is asked again at every reading,
/// also of every line of the account that already waited, whatever its age: a line stored before
/// this rule existed, a transaction typed, captured, imported or edited after the line arrived, a settled line the
/// bank corrects later.
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
    /// longer lists them, as a pending purchase the bank settled under another id). A line of
    /// <paramref name="lines"/> judged before (a pending purchase settled under the same id) keeps the transaction it
    /// points to while that one is still there and still looks like it; otherwise it is judged as a new one, and
    /// loses the suspicion when nothing looks like it any more (also when it is no longer in reais or has no value).
    /// Every other line of the account that already waited for the review is judged as a suspected duplicate in the
    /// same way, and never ignored here (rules 1 to 3 are only for <paramref name="lines"/>):
    /// <paramref name="readAgain"/> are the ones this reading has in hand (the bank listed them again, maybe
    /// corrected), judged as they are now; the others are judged as the store has them
    /// (<see cref="IBankSyncRepository.GetWaitingOpenToRulesForJobAsync"/>), and only the ones whose suspicion
    /// changes are read whole.
    /// </summary>
    public async Task ApplyAsync(
        Guid coupleId,
        BankAccount account,
        string? connectorName,
        IReadOnlyList<ArrivedBankLine> lines,
        IReadOnlyCollection<BankTransaction> readAgain,
        IReadOnlyCollection<BankTransaction> leaving,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        // Oldest first, so that the same data always pairs the same way.
        var open = lines
            .Where(l => l.Row.OpenToAutomaticRules && BankReconciliationRules.HasComparableValue(FactsOf(l.Row)))
            .OrderBy(l => l.Row.Date)
            .ThenBy(l => l.Row.PluggyTransactionId, StringComparer.Ordinal)
            .ToList();
        if (open.Count > 0) await IgnoreWhatIsNoExpenseNorIncomeAsync(coupleId, account, open, now, ct);

        // What was not ignored: which transaction of the app each line looks like, asked of the store for each one.
        // The lines in hand are judged as they are now, whatever the store still says of them.
        var inHand = lines.Select(l => l.Row).Concat(readAgain).Distinct().ToList();
        var leavingIds = leaving.Select(l => l.Id).ToList();
        var notFromTheStore = inHand.Select(l => l.Id).Concat(leavingIds).ToHashSet();
        var waiting = inHand.Where(row => row.OpenToAutomaticRules).ToDictionary(row => row.Id);
        var stored = (await _sync.GetWaitingOpenToRulesForJobAsync(coupleId, account.Id, ct))
            .Where(l => !notFromTheStore.Contains(l.Id))
            .ToList();

        var answers = await BankSuspicionJudge.AnswerAsync(
            _sync,
            coupleId,
            waiting.Values.Select(row => JudgedLine.Of(row, connectorName))
                .Concat(stored.Select(l => JudgedLine.Of(l, account.Id, connectorName))),
            leavingIds,
            takenTransactionIds: [],
            ct);

        // Of the lines judged as the store has them, only the ones whose suspicion changes are read whole.
        var changed = answers
            .Where(a => !waiting.ContainsKey(a.Line.Id) && a.Suspect != a.Line.MatchedTransactionId)
            .Select(a => a.Line.Id)
            .ToList();
        if (changed.Count > 0)
        {
            foreach (var row in await _sync.FindByIdsForJobAsync(changed, coupleId, ct))
                if (row.OpenToAutomaticRules) waiting[row.Id] = row;
        }

        foreach (var (line, suspect) in answers)
            if (waiting.TryGetValue(line.Id, out var row)) BankSuspicionJudge.Apply(row, suspect, now);
    }

    /// <summary>
    /// Judges, as suspected duplicates only, every line of the group <paramref name="coupleId"/> that waits for the
    /// review and is still open to the automatic rules, in every account: also the ones no run reads any more
    /// (synchronisation turned off, a connection disconnected or whose bank asks for the login again). Nothing is
    /// asked of the bank and nothing is ignored here (rules 1 to 3 are for what arrives); a line a person brought
    /// back to the review is left as it is. The lines are judged as the store has them and only the ones whose
    /// suspicion changes are read whole. Answers whether any changed (the caller saves).
    /// </summary>
    public async Task<bool> JudgeWaitingOfGroupAsync(Guid coupleId, CancellationToken ct)
    {
        var stored = await _sync.GetWaitingOpenToRulesOfGroupForJobAsync(coupleId, ct);
        if (stored.Count == 0) return false;

        var connectors = await _sync.GetConnectorNamesForJobAsync(coupleId, ct);
        var answers = await BankSuspicionJudge.AnswerAsync(
            _sync,
            coupleId,
            stored.Select(l => JudgedLine.Of(l, l.BankAccountId, connectors.GetValueOrDefault(l.BankAccountId))),
            leavingLineIds: [],
            takenTransactionIds: [],
            ct);

        var changed = answers
            .Where(a => a.Suspect != a.Line.MatchedTransactionId)
            .ToDictionary(a => a.Line.Id, a => a.Suspect);
        if (changed.Count == 0) return false;

        var now = _clock.UtcNow;
        var any = false;
        foreach (var row in await _sync.FindByIdsForJobAsync(changed.Keys.ToList(), coupleId, ct))
        {
            if (!row.OpenToAutomaticRules) continue;
            BankSuspicionJudge.Apply(row, changed[row.Id], now);
            any = true;
        }

        return any;
    }

    /// <summary>Rules 1 to 3, over the lines the bank has settled: what it has not may still change or vanish.</summary>
    private async Task IgnoreWhatIsNoExpenseNorIncomeAsync(
        Guid coupleId, BankAccount account, IReadOnlyList<ArrivedBankLine> open, DateTime now, CancellationToken ct)
    {
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

        // What the bank has not settled may still change or vanish: it is never ignored, only suspected (by the caller).
        foreach (var line in settled)
        {
            var row = line.Row;
            var facts = FactsOf(row);

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
                row.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, now);
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
