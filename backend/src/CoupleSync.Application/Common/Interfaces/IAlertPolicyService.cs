using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IAlertPolicyService
{
    /// <summary>
    /// Alerts raised by a newly persisted transaction, one event per recipient (every member of the couple,
    /// each one's notification settings respected). The author is <paramref name="newTransaction"/>.UserId.
    /// </summary>
    Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
        Guid coupleId,
        Transaction newTransaction,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Alerts raised by the transactions created by ONE statement import confirmation (all by the same author).
    /// Same rules as <see cref="EvaluatePostIngestAsync"/>, applied once to the whole import: several lines above
    /// the large-transaction threshold produce a single summary alert, not one per line.
    /// </summary>
    Task<IReadOnlyList<NotificationEvent>> EvaluatePostImportAsync(
        Guid coupleId,
        IReadOnlyList<Transaction> importedTransactions,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Alerts raised by the transactions created by ONE confirmation of the review of the bank (Open Finance).
    /// The rules of <see cref="EvaluatePostImportAsync"/>; the summary of several large lines says they were
    /// confirmed in the review of the bank, not imported from a statement.
    /// </summary>
    Task<IReadOnlyList<NotificationEvent>> EvaluatePostBankReviewAsync(
        Guid coupleId,
        IReadOnlyList<Transaction> confirmedTransactions,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default);
}
