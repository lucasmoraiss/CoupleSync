using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeIncomeSourceRepository : IIncomeSourceRepository
{
    public List<IncomeSource> Sources { get; } = new();

    public Task<IReadOnlyList<IncomeSource>> GetByMonthAsync(Guid coupleId, string month, CancellationToken ct)
    {
        IReadOnlyList<IncomeSource> result = Sources
            .Where(s => s.CoupleId == coupleId && s.Month == month)
            .OrderBy(s => s.CreatedAtUtc)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IncomeSource?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
    {
        var source = Sources.FirstOrDefault(s => s.Id == id && s.CoupleId == coupleId);
        return Task.FromResult(source);
    }

    public Task<int> CountByUserAndMonthAsync(Guid userId, Guid coupleId, string month, CancellationToken ct)
    {
        var count = Sources.Count(s => s.UserId == userId && s.CoupleId == coupleId && s.Month == month);
        return Task.FromResult(count);
    }

    public Task AddAsync(IncomeSource source, CancellationToken ct)
    {
        Sources.Add(source);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IncomeSource source, CancellationToken ct)
    {
        Sources.Remove(source);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => Task.CompletedTask;
}
