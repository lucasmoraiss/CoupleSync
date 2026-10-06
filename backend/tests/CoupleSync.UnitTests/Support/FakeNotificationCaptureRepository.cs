using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeNotificationCaptureRepository : INotificationCaptureRepository
{
    public List<TransactionEventIngest> IngestEvents { get; } = new();

    public Task AddIngestEventAsync(TransactionEventIngest ingestEvent, CancellationToken cancellationToken)
    {
        IngestEvents.Add(ingestEvent);
        return Task.CompletedTask;
    }

    public Task AddIngestEventsRangeAsync(IEnumerable<TransactionEventIngest> ingestEvents, CancellationToken cancellationToken)
    {
        IngestEvents.AddRange(ingestEvents);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private IEnumerable<TransactionEventIngest> FromNotifications(Guid coupleId)
        => IngestEvents.Where(e => e.CoupleId == coupleId && !TransactionEventIngest.NonNotificationBanks.Contains(e.Bank));

    public Task<int> CountByStatusAsync(Guid coupleId, IngestStatus status, CancellationToken cancellationToken)
    {
        var count = FromNotifications(coupleId).Count(e => e.Status == status);
        return Task.FromResult(count);
    }

    public Task<TransactionEventIngest?> GetLastByStatusAsync(Guid coupleId, IngestStatus status, CancellationToken cancellationToken)
    {
        var result = FromNotifications(coupleId)
            .Where(e => e.Status == status)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(result);
    }

    public Task<DateTime?> GetLastEventTimeAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        var result = FromNotifications(coupleId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => (DateTime?)e.CreatedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(result);
    }
}
