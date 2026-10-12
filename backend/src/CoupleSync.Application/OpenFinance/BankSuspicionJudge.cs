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
        var waiting = lines.Where(l => l.ReviewState == BankTransactionReviewState.Pending).DistinctBy(l => l.Id).ToDictionary(l => l.Id);
        var answers = await AnswerAsync(
            sync, coupleId, waiting.Values.Select(l => JudgedLine.Of(l, connectorNameOf(l))), leavingLineIds, takenTransactionIds, ct);
        foreach (var (line, suspect) in answers) Apply(waiting[line.Id], suspect, nowUtc);
    }

    /// <summary>
    /// The answer for each of <paramref name="lines"/> (each one waiting for the review): the transaction it looks
    /// like, or none. Nothing is changed here: <see cref="Apply"/> writes an answer on a line.
    /// </summary>
    public static async Task<IReadOnlyList<(JudgedLine Line, Guid? Suspect)>> AnswerAsync(
        IBankSyncRepository sync,
        Guid coupleId,
        IEnumerable<JudgedLine> lines,
        IReadOnlyCollection<Guid> leavingLineIds,
        IReadOnlyCollection<Guid> takenTransactionIds,
        CancellationToken ct)
    {
        // Oldest first, so that the same data always gives the same answer.
        var waiting = lines
            .OrderBy(l => l.Date)
            .ThenBy(l => l.PluggyTransactionId, StringComparer.Ordinal)
            .ToList();
        if (waiting.Count == 0) return [];

        var questions = waiting
            .Select(l => new SuspicionQuestion(l.Facts, l.MatchedTransactionId, l.ConnectorName))
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
        return waiting.Select((line, i) => (line, suspects[i]?.Id)).ToList();
    }

    /// <summary>Writes the answer on the line: it keeps, takes or loses its suspicion (only while it waits for the review).</summary>
    public static void Apply(BankTransaction line, Guid? suspect, DateTime nowUtc)
    {
        if (suspect is not { } transactionId) line.ClearSuspectedDuplicate(nowUtc);
        else if (line.MatchedTransactionId != transactionId) line.MarkSuspectedDuplicate(transactionId, nowUtc);
    }

    /// <summary>The name of the bank of each line, by its account (<see cref="IBankSyncRepository.GetAccountNamesAsync"/>).</summary>
    public static Func<BankTransaction, string?> ConnectorNameBy(IReadOnlyDictionary<Guid, BankAccountNames> names)
        => line => names.TryGetValue(line.BankAccountId, out var account) ? account.BankName : null;
}

/// <summary>
/// A line waiting for the review as the judgement sees it: in the hand of who asks (<see cref="Of(BankTransaction, string?)"/>)
/// or as the store has it, without the whole line being read (<see cref="Of(WaitingBankLine, Guid, string?)"/>).
/// </summary>
public sealed record JudgedLine(
    Guid Id, DateTime Date, string PluggyTransactionId, MoneyMovement Facts, Guid? MatchedTransactionId, string? ConnectorName)
{
    public static JudgedLine Of(BankTransaction line, string? connectorName) => new(
        line.Id, line.Date, line.PluggyTransactionId, BankPreclassificationService.FactsOf(line), line.MatchedTransactionId, connectorName);

    public static JudgedLine Of(WaitingBankLine line, Guid bankAccountId, string? connectorName) => new(
        line.Id,
        line.Date,
        line.PluggyTransactionId,
        new MoneyMovement(
            Math.Abs(line.Amount),
            line.Currency,
            line.LocalDate,
            line.Type == BankTransactionType.Debit ? MoneyDirection.Out : MoneyDirection.In,
            bankAccountId),
        line.MatchedTransactionId,
        connectorName);
}
