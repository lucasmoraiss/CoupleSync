namespace CoupleSync.Application.Common.Interfaces;

public interface ICategoryClassifier
{
    /// <summary>
    /// Suggests a category for the given transaction description from the provided list.
    /// Returns <c>null</c> if no confident match is found or if the underlying
    /// AI service is unavailable.
    /// </summary>
    Task<string?> SuggestCategoryAsync(
        string description,
        IReadOnlyList<string> availableCategories,
        CancellationToken ct);
}
