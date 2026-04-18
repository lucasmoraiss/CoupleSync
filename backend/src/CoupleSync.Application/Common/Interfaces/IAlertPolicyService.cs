using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IAlertPolicyService
{
    Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
        Guid coupleId,
        Guid userId,
        Transaction newTransaction,
        IReadOnlyList<Transaction> recentTransactions,
        NotificationSettings settings,
        DateTime nowUtc,
        CancellationToken ct = default);
}
