using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeCategoryRuleRepository : ICategoryRuleRepository
{
    public List<CategoryRule> Rules { get; } = new();

    public Task<List<CategoryRule>> GetActiveRulesAsync(CancellationToken ct)
    {
        return Task.FromResult(Rules.Where(r => r.IsActive).OrderByDescending(r => r.Priority).ToList());
    }

    public Task<bool> KeywordExistsAsync(string keyword, CancellationToken ct)
    {
        return Task.FromResult(Rules.Any(r => r.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    public Task AddRuleAsync(CategoryRule rule, CancellationToken ct)
    {
        Rules.Add(rule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
