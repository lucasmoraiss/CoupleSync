using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface INotificationCaptureRepository
{
    Task AddIngestEventAsync(TransactionEventIngest ingestEvent, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<int> CountByStatusAsync(Guid coupleId, IngestStatus status, CancellationToken cancellationToken);
    Task<TransactionEventIngest?> GetLastByStatusAsync(Guid coupleId, IngestStatus status, CancellationToken cancellationToken);
    Task<DateTime?> GetLastEventTimeAsync(Guid coupleId, CancellationToken cancellationToken);
}
