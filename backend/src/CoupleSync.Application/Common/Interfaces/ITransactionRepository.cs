using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface ITransactionRepository
{
    Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct);
    Task AddTransactionAsync(Transaction transaction, CancellationToken ct);
    Task<(int TotalCount, IReadOnlyList<Transaction> Items)> GetPagedAsync(
        Guid coupleId,
        int page,
        int pageSize,
        string? category,
        DateTime? startDate,
        DateTime? endDate,
        CancellationToken ct);
    Task<Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task<Transaction?> GetByIdRawAsync(Guid id, CancellationToken ct);
    Task DeleteAsync(Transaction transaction, CancellationToken ct);
    Task<IReadOnlyList<Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct);
    Task<IReadOnlyList<Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct);
    Task UpdateAsync(Transaction transaction, CancellationToken ct = default);
    Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(Guid coupleId, DateTime startUtc, DateTime endUtc, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
