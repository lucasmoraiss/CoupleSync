using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeAlertPolicyService : IAlertPolicyService
{
    public IReadOnlyList<NotificationEvent> EventsToReturn { get; set; } = [];

    public Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
        Guid coupleId,
        Transaction newTransaction,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        return Task.FromResult(EventsToReturn);
    }

    public Task<IReadOnlyList<NotificationEvent>> EvaluatePostImportAsync(
        Guid coupleId,
        IReadOnlyList<Transaction> importedTransactions,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        return Task.FromResult(EventsToReturn);
    }

    public Task<IReadOnlyList<NotificationEvent>> EvaluatePostBankReviewAsync(
        Guid coupleId,
        IReadOnlyList<Transaction> confirmedTransactions,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        return Task.FromResult(EventsToReturn);
    }
}
