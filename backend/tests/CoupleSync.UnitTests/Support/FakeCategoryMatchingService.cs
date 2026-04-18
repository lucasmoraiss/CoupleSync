using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeCategoryMatchingService : ICategoryMatchingService
{
    public string DefaultCategory { get; set; } = "OUTROS";
    private readonly Dictionary<string, string> _rules = new(StringComparer.OrdinalIgnoreCase);

    public void AddRule(string keyword, string category)
    {
        _rules[keyword] = category;
    }

    public Task<string> MatchCategoryAsync(string? description, string? merchant, CancellationToken ct)
    {
        var descUpper = (description ?? "").ToUpperInvariant();
        var merchantUpper = (merchant ?? "").ToUpperInvariant();

        foreach (var rule in _rules)
        {
            if (descUpper.Contains(rule.Key, StringComparison.OrdinalIgnoreCase) ||
                merchantUpper.Contains(rule.Key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(rule.Value);
            }
        }

        return Task.FromResult(DefaultCategory);
    }
}
