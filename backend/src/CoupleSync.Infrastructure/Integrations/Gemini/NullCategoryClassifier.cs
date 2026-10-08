using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Integrations.Gemini;

/// <summary>
/// No-op implementation: the one registered while statement categorization by AI is off (see DependencyInjection).
/// Always returns <c>null</c>; transactions will receive the default category.
/// </summary>
public sealed class NullCategoryClassifier : ICategoryClassifier
{
    public Task<string?> SuggestCategoryAsync(
        string description,
        IReadOnlyList<string> availableCategories,
        CancellationToken ct)
        => Task.FromResult<string?>(null);
}
