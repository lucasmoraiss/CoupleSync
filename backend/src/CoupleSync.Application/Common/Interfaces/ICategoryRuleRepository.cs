using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface ICategoryRuleRepository
{
    Task<List<CategoryRule>> GetActiveRulesAsync(CancellationToken ct);
    Task<bool> KeywordExistsAsync(string keyword, CancellationToken ct);
    Task AddRuleAsync(CategoryRule rule, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
