using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

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
/// <item>an expense that looks like a transaction the app already has: it stays waiting, pointing to that one.</item>
/// </list>
/// Only lines <see cref="BankTransaction.OpenToAutomaticRules"/>, in reais and with a value. What is ignored was
/// settled at the bank: a line the bank may still change or drop is never ignored. Nothing is saved here: the run
/// saves, and a line someone reviewed meanwhile keeps that review.
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
    /// </summary>
    public async Task ApplyAsync(
        Guid coupleId, BankAccount account, string? connectorName, IReadOnlyList<ArrivedBankLine> lines, CancellationToken ct)
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

        IReadOnlyList<BankTransaction> otherLegs = settled.Count > 0
            ? await _sync.GetTransferCandidatesForJobAsync(coupleId, settled.Min(l => l.Row.LocalDate), settled.Max(l => l.Row.LocalDate), ct)
            : [];

        IReadOnlyList<string?> holdersOfOtherAccounts =
            settled.Any(l => !l.Row.IsExpense && !string.IsNullOrWhiteSpace(l.PayerName))
                ? (await _sync.GetAccountHoldersForJobAsync(coupleId, ct))
                    .Where(h => h.AccountId != account.Id)
                    .Select(h => h.UserName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [];

        var existing = open.Any(l => l.Row.IsExpense)
            ? (await _sync.GetDuplicateCandidatesForJobAsync(
                coupleId,
                BrazilTime.ToUtc(firstDay.AddDays(-BankReconciliationRules.DuplicateDayTolerance).ToDateTime(TimeOnly.MinValue)),
                BrazilTime.ToUtc(lastDay.AddDays(BankReconciliationRules.DuplicateDayTolerance + 1).ToDateTime(TimeOnly.MinValue)),
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
                var otherLeg = BankReconciliationRules.FindTransferCounterpart(facts, otherLegs.Where(c => c.CanBePairedAsTransfer), FactsOf);
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
}
