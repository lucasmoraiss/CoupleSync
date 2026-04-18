using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeAlertPolicyService : IAlertPolicyService
{
    public IReadOnlyList<NotificationEvent> EventsToReturn { get; set; } = [];

    public Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
        Guid coupleId,
        Guid userId,
        Transaction newTransaction,
        IReadOnlyList<Transaction> recentTransactions,
        NotificationSettings settings,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        return Task.FromResult(EventsToReturn);
    }
}
