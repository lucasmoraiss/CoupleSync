using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Infrastructure.Integrations.Gemini;

/// <summary>
/// AI-powered transaction categorizer backed by Gemini Flash.
/// Implements fail-fast: after <see cref="MaxConsecutiveErrors"/> consecutive
/// Gemini errors the classifier short-circuits and returns <c>null</c> for all
/// remaining calls within the same scope lifetime.
/// Per-call timeout: 3 seconds (linked to caller's CancellationToken).
/// </summary>
public sealed class GeminiCategoryClassifier : ICategoryClassifier
{
    private readonly IGeminiAdapter _gemini;
    private int _consecutiveErrors;

    private const int MaxConsecutiveErrors = 2;
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(3);

    public static readonly string[] DefaultCategories =
    [
        "Alimentação", "Transporte", "Moradia", "Saúde", "Educação",
        "Lazer", "Vestuário", "Serviços", "Investimentos", "Outros"
    ];

    public GeminiCategoryClassifier(IGeminiAdapter gemini)
    {
        _gemini = gemini;
    }

    public async Task<string?> SuggestCategoryAsync(
        string description,
        IReadOnlyList<string> availableCategories,
        CancellationToken ct)
    {
        if (_consecutiveErrors >= MaxConsecutiveErrors)
            return null;

        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCallCts.CancelAfter(PerCallTimeout);

        try
        {
            var userMessage = BuildPrompt(description, availableCategories);
            var response = await _gemini.SendAsync(
                string.Empty, Array.Empty<ChatMessage>(), userMessage, perCallCts.Token);

            // Successful Gemini call — reset error counter
            _consecutiveErrors = 0;

            var trimmed = response?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            return availableCategories.FirstOrDefault(c =>
                string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            _consecutiveErrors++;
            return null;
        }
    }

    /// <summary>
    /// Builds the classification prompt per ADR-004.
    /// The transaction description is placed inside triple-double-quote delimiters
    /// in a dedicated section, clearly separated from the instruction text.
    /// Bare double-quote characters are stripped from description before interpolation
    /// to prevent prompt injection — NFR-003.
    /// </summary>
    private static string BuildPrompt(string description, IReadOnlyList<string> availableCategories)
    {
        var sanitizedDescription = description.Replace("\"", string.Empty);
        var categoryList = string.Join("\n", availableCategories.Take(30));

        return
            "You are a Brazilian personal finance transaction categorizer.\n" +
            "Available categories (reply with ONLY one of these, exactly as written, or \"Outros\" if none match):\n" +
            categoryList + "\n\n" +
            "Transaction description:\n" +
            "\"\"\"" + sanitizedDescription + "\"\"\"\n\n" +
            "Reply with only the category name and nothing else.";
    }
}
