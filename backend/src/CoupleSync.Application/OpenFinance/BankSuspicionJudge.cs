using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// The one place that says which transaction of the app a waiting line of the bank looks like (issue #26), so that
/// this holds whatever happened before and in whatever order: <b>every line waiting for the review, an expense in
/// reais with a value, that has at least one transaction it may still be the same as, points to one of them</b>.
/// "May still be the same as": of the group, the same value, the same day in Brazil or one day apart, not of another
/// bank the app knows, not come from the bank, and not linked to any line of the mirror yet
/// (<see cref="BankReconciliationRules.IsSuspectedDuplicate"/> over
/// <see cref="IBankSyncRepository.GetDuplicateCandidatesForJobAsync"/>). The answer for a line is read from the
/// store and does not depend on the other lines: several lines may point to the same transaction.
/// <para>
/// It is asked whenever a line starts to wait or the transaction it points to stops being one to point to: a line
/// arrives or is settled at the bank (the synchronisation), the person brings a line back to the review, the
/// transaction is linked to another line ("it is the same") or deleted. Nothing is saved here: the caller saves the
/// lines together with what made them be judged (the deletion saves them right after itself).
/// </para>
/// <para>
/// A transaction typed, captured, imported or edited after the line arrived does not ask it: the line is judged at
/// the next synchronisation of its account, which asks it of every line of the account that already waited and
/// that no person brought back to the review. The ones a person brought back wait as they are.
/// </para>
/// </summary>
public static class BankSuspicionJudge
{
    /// <summary>
    /// Judges <paramref name="lines"/> (tracked; the ones that no longer wait are left alone): each one keeps, takes
    /// or loses its suspicion. <paramref name="leavingLineIds"/> are lines going out of the mirror in the same save,
    /// and <paramref name="takenTransactionIds"/> the transactions that stop being candidates in the same save
    /// (linked to a line, or deleted): the store does not know either yet.
    /// </summary>
    public static async Task JudgeAsync(
        IBankSyncRepository sync,
        Guid coupleId,
        IEnumerable<BankTransaction> lines,
        Func<BankTransaction, string?> connectorNameOf,
        IReadOnlyCollection<Guid> leavingLineIds,
        IReadOnlyCollection<Guid> takenTransactionIds,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Oldest first, so that the same data always gives the same answer.
        var waiting = lines
            .Where(l => l.ReviewState == BankTransactionReviewState.Pending)
            .OrderBy(l => l.Date)
            .ThenBy(l => l.PluggyTransactionId, StringComparer.Ordinal)
            .ToList();
        if (waiting.Count == 0) return;

        var questions = waiting
            .Select(l => new SuspicionQuestion(BankPreclassificationService.FactsOf(l), l.MatchedTransactionId, connectorNameOf(l)))
            .ToList();

        // Money that came in, another currency, no value: nothing to look for, and a suspicion it carried is cleared.
        var days = questions
            .Where(q => q.Line.Direction == MoneyDirection.Out && BankReconciliationRules.HasComparableValue(q.Line))
            .Select(q => q.Line.LocalDay)
            .ToList();
        IReadOnlyList<ExistingTransactionFacts> candidates = [];
        if (days.Count > 0)
        {
            var (fromUtc, toUtcExclusive) = BankReconciliationRules.DuplicateSearchWindowUtc(days.Min(), days.Max());
            candidates = (await sync.GetDuplicateCandidatesForJobAsync(
                    coupleId, fromUtc, toUtcExclusive, waiting.Select(l => l.Id).Concat(leavingLineIds).ToList(), ct))
                .Where(c => !takenTransactionIds.Contains(c.Id))
                .ToList();
        }

        var suspects = BankReconciliationRules.AssignSuspectedDuplicates(questions, candidates);
        for (var i = 0; i < waiting.Count; i++)
        {
            if (suspects[i] is not { } suspect) waiting[i].ClearSuspectedDuplicate(nowUtc);
            else if (waiting[i].MatchedTransactionId != suspect.Id) waiting[i].MarkSuspectedDuplicate(suspect.Id, nowUtc);
        }
    }

    /// <summary>The name of the bank of each line, by its account (<see cref="IBankSyncRepository.GetAccountNamesAsync"/>).</summary>
    public static Func<BankTransaction, string?> ConnectorNameBy(IReadOnlyDictionary<Guid, BankAccountNames> names)
        => line => names.TryGetValue(line.BankAccountId, out var account) ? account.BankName : null;
}
