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
}
