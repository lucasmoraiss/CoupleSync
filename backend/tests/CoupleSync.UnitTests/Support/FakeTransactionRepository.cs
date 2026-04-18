using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeTransactionRepository : ITransactionRepository
{
    public List<Transaction> Transactions { get; } = new();
    private readonly HashSet<string> _existingFingerprints = new();

    public void AddExistingFingerprint(string fingerprint, Guid coupleId)
    {
        _existingFingerprints.Add($"{coupleId}|{fingerprint}");
    }

    public Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct)
    {
        return Task.FromResult(_existingFingerprints.Contains($"{coupleId}|{fingerprint}"));
    }

    public Task AddTransactionAsync(Transaction transaction, CancellationToken ct)
    {
        Transactions.Add(transaction);
        return Task.CompletedTask;
    }

    public Task<(int TotalCount, IReadOnlyList<Transaction> Items)> GetPagedAsync(
        Guid coupleId,
        int page,
        int pageSize,
        string? category,
        DateTime? startDate,
        DateTime? endDate,
        CancellationToken ct)
    {
        var query = Transactions.Where(t => t.CoupleId == coupleId);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category == category);

        if (startDate.HasValue)
            query = query.Where(t => t.EventTimestampUtc >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(t => t.EventTimestampUtc <= endDate.Value);

        var ordered = query.OrderByDescending(t => t.EventTimestampUtc).ToList();
        var totalCount = ordered.Count;
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult<(int, IReadOnlyList<Transaction>)>((totalCount, items));
    }

    public Task<Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
    {
        return Task.FromResult(Transactions.FirstOrDefault(t => t.Id == id && t.CoupleId == coupleId));
    }

    public Task<IReadOnlyList<Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct)
    {
        IReadOnlyList<Transaction> result = Transactions
            .Where(t => t.GoalId == goalId && t.CoupleId == coupleId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct)
    {
        var result = Transactions
            .Where(t => t.CoupleId == coupleId && t.EventTimestampUtc >= since)
            .ToList();
        return Task.FromResult<IReadOnlyList<Transaction>>(result);
    }

    public Task UpdateAsync(Transaction transaction, CancellationToken ct = default)
    {
        UpdateCalled = true;
        return Task.CompletedTask;
    }

    public bool UpdateCalled { get; private set; }

    public Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(
        Guid coupleId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken ct)
    {
        var result = Transactions
            .Where(t => t.CoupleId == coupleId
                        && t.EventTimestampUtc >= startUtc
                        && t.EventTimestampUtc < endUtc)
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        return Task.FromResult(result);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
