using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class NotificationCaptureRepository : INotificationCaptureRepository
{
    private readonly AppDbContext _dbContext;

    public NotificationCaptureRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddIngestEventAsync(TransactionEventIngest ingestEvent, CancellationToken cancellationToken)
    {
        await _dbContext.TransactionEventIngests.AddAsync(ingestEvent, cancellationToken);
    }

    public async Task AddIngestEventsRangeAsync(IEnumerable<TransactionEventIngest> ingestEvents, CancellationToken cancellationToken)
    {
        await _dbContext.TransactionEventIngests.AddRangeAsync(ingestEvents, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    // Manual entries and OCR imports also write ingest rows; the integration status is about
    // notification capture only.
    private IQueryable<TransactionEventIngest> CapturedFromNotifications(Guid coupleId)
    {
        return _dbContext.TransactionEventIngests
            .IgnoreQueryFilters()
            .Where(e => e.CoupleId == coupleId && !TransactionEventIngest.NonNotificationBanks.Contains(e.Bank));
    }

    public Task<int> CountByStatusAsync(Guid coupleId, IngestStatus status, CancellationToken cancellationToken)
    {
        return CapturedFromNotifications(coupleId)
            .CountAsync(e => e.Status == status, cancellationToken);
    }

    public Task<TransactionEventIngest?> GetLastByStatusAsync(Guid coupleId, IngestStatus status, CancellationToken cancellationToken)
    {
        return CapturedFromNotifications(coupleId)
            .Where(e => e.Status == status)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<DateTime?> GetLastEventTimeAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return CapturedFromNotifications(coupleId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => (DateTime?)e.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
