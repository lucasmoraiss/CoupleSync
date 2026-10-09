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
/// <item>the payment of a credit card bill: ignored;</item>
/// <item>a credit paid by who holds another account of the group: ignored;</item>
/// <item>an expense that looks like a transaction the app already has: it stays waiting, pointing to that one
/// (<see cref="BankReconciliationRules.AssignSuspectedDuplicates"/>: a line judged again keeps the transaction it
/// pointed to, the same day wins over the day next to it, one transaction is of one line only).</item>
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
    /// it (a pending purchase the bank settled under another id). A line of <paramref name="lines"/> judged before (a
    /// pending purchase settled under the same id) keeps the transaction it points to while that one is still there
    /// and still looks like it; otherwise the suspicion is cleared and the line is judged as a new one.
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

        // What a line that is leaving holds is free again; what a line judged here holds is its own to keep, and is
        // among the candidates only for that.
        var (fromUtc, toUtcExclusive) = BankReconciliationRules.DuplicateSearchWindowUtc(firstDay, lastDay);
        var existing = open.Any(l => l.Row.IsExpense)
            ? await _sync.GetDuplicateCandidatesForJobAsync(
                coupleId,
                fromUtc,
                toUtcExclusive,
                leaving.Concat(open.Select(l => l.Row)).Where(l => l.MatchedTransactionId is not null).Select(l => l.Id).ToList(),
                ct)
            : [];

        var now = _clock.UtcNow;
        // What the bank has not settled may still change or vanish: it is never ignored, only (below) suspected.
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
                continue;
            }

            if (BankReconciliationRules.IsBillPayment(facts, account.Type, row.DescriptionRaw ?? row.Description))
            {
                row.IgnoreAutomatically(BankTransactionAutoReasons.BillPayment, now);
                continue;
            }

            if (BankReconciliationRules.IsCreditFromGroupHolder(facts, line.PayerName, holdersOfOtherAccounts))
                row.IgnoreAutomatically(BankTransactionAutoReasons.Transfer, now);
        }

        // What was not ignored, all at once: which transaction of the app each line looks like.
        var waiting = open.Where(l => l.Row.OpenToAutomaticRules).ToList();
        var suspects = BankReconciliationRules.AssignSuspectedDuplicates(
            waiting.Select(l => new SuspicionQuestion(FactsOf(l.Row), l.Row.MatchedTransactionId)).ToList(), connectorName, existing);
        for (var i = 0; i < waiting.Count; i++)
        {
            var row = waiting[i].Row;
            if (suspects[i] is not { } suspect) row.ClearSuspectedDuplicate(now);
            else if (row.MatchedTransactionId != suspect.Id) row.MarkSuspectedDuplicate(suspect.Id, now);
        }
    }
}
