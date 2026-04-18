using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Security;

public sealed class CategoryMatchingService : ICategoryMatchingService
{
    private readonly ICategoryRuleRepository _ruleRepository;

    public CategoryMatchingService(ICategoryRuleRepository ruleRepository)
    {
        _ruleRepository = ruleRepository;
    }

    public async Task<string> MatchCategoryAsync(string? description, string? merchant, CancellationToken ct)
    {
        var rules = await _ruleRepository.GetActiveRulesAsync(ct);

        var descUpper = (description ?? "").ToUpperInvariant();
        var merchantUpper = (merchant ?? "").ToUpperInvariant();

        foreach (var rule in rules) // already ordered by priority DESC
        {
            var keywordUpper = rule.Keyword.ToUpperInvariant();
            if (descUpper.Contains(keywordUpper) || merchantUpper.Contains(keywordUpper))
            {
                return rule.Category;
            }
        }

        return "OUTROS";
    }
}
