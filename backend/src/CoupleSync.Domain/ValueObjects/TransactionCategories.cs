namespace CoupleSync.Domain.ValueObjects;

/// <summary>A canonical category: the stored key and the Portuguese label shown to users.</summary>
public sealed record CategoryDefinition(string Key, string Label);

/// <summary>
/// Single source of truth for transaction / budget / rule categories (DAD-07).
/// Keys are upper case without accents; inputs in any case, with or without accents and with
/// surrounding spaces ("alimentacao", " Alimentação ") converge to the key. The mobile app keeps an
/// offline copy of this list (mobile/src/modules/transactions/categories.ts) locked by a test.
/// </summary>
public static class TransactionCategories
{
    public const string Other = "OUTROS";

    public static IReadOnlyList<CategoryDefinition> All { get; } =
    [
        new("ALIMENTACAO", "Alimentação"),
        new("TRANSPORTE", "Transporte"),
        new("COMPRAS", "Compras"),
        new("SAUDE", "Saúde"),
        new("LAZER", "Lazer"),
        new("MORADIA", "Moradia"),
        new(Other, "Outros"),
    ];

    private static readonly HashSet<string> Keys = All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Lower-level fold shared with the data migration: trim, drop accents, upper case. Uses the explicit
    /// <see cref="AccentFolding"/> table, never Unicode normalization, so the answer is the same with or
    /// without ICU on the host.
    /// </summary>
    public static string Fold(string value)
        => AccentFolding.RemoveAccents(value.Trim()).ToUpperInvariant();

    /// <summary>The canonical key for the input, or null when it matches no category.</summary>
    public static string? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var folded = Fold(value);
        return Keys.Contains(folded) ? folded : null;
    }

    /// <summary>The canonical key for the input; anything unrecognised (or empty) becomes OUTROS.</summary>
    public static string NormalizeOrOther(string? value) => TryNormalize(value) ?? Other;

    public static string Label(string? value)
    {
        var key = NormalizeOrOther(value);
        return All.First(c => c.Key == key).Label;
    }

    /// <summary>Portuguese message for a rejected category, listing what is accepted.</summary>
    public static string InvalidMessage { get; } =
        "Categoria inválida. Categorias aceitas: " + string.Join(", ", All.Select(c => c.Key)) + ".";
}
