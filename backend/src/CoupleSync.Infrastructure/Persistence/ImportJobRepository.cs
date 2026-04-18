using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class ImportJobRepository : IImportJobRepository
{
    private readonly AppDbContext _dbContext;

    public ImportJobRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
        => _dbContext.ImportJobs
            .FirstOrDefaultAsync(j => j.Id == id && j.CoupleId == coupleId, ct);

    public Task AddAsync(ImportJob job, CancellationToken ct)
        => _dbContext.ImportJobs.AddAsync(job, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _dbContext.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct)
        => await _dbContext.ImportJobs
            .Where(j => j.Status == ImportJobStatus.Pending)
            .OrderBy(j => j.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
}
