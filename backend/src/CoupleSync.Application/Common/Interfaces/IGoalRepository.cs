using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IGoalRepository
{
    Task<(int TotalCount, IReadOnlyList<Goal> Items)> GetPagedAsync(
        Guid coupleId,
        bool includeArchived,
        CancellationToken ct);
    Task<Goal?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task AddAsync(Goal goal, CancellationToken ct);
    /// <summary>
    /// Deletes the goal right away, after unlinking it from every transaction of the couple that points at it
    /// (the transactions themselves stay).
    /// </summary>
    Task DeleteAsync(Goal goal, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
