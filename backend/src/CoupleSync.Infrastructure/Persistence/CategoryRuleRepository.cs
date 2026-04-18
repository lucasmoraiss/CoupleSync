using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class CategoryRuleRepository : ICategoryRuleRepository
{
    private readonly AppDbContext _dbContext;

    public CategoryRuleRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<CategoryRule>> GetActiveRulesAsync(CancellationToken ct)
    {
        return await _dbContext.CategoryRules
            .Where(r => r.IsActive)
            .OrderByDescending(r => r.Priority)
            .ToListAsync(ct);
    }

    public async Task<bool> KeywordExistsAsync(string keyword, CancellationToken ct)
    {
        return await _dbContext.CategoryRules
            .AnyAsync(r => r.Keyword == keyword, ct);
    }

    public async Task AddRuleAsync(CategoryRule rule, CancellationToken ct)
    {
        await _dbContext.CategoryRules.AddAsync(rule, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}
