using System.Text;

namespace CoupleSync.Domain.ValueObjects;

/// <summary>
/// The explicit accent map behind category matching. It is a table on purpose: <c>string.Normalize</c> and
/// <c>CharUnicodeInfo</c> need ICU, and in a globalization-invariant host (minimal containers) they silently
/// leave the text unchanged. The same pairs build the SQL of the NormalizeCategoriesAndCurrency migration,
/// so C# and the database fold text the same way.
/// </summary>
public static class AccentFolding
{
    /// <summary>(accented, plain) pairs, lower and upper case, plus the combining marks of decomposed text.</summary>
    public static IReadOnlyList<(string From, string To)> Pairs { get; } =
    [
        ("á", "a"), ("à", "a"), ("â", "a"), ("ã", "a"), ("ä", "a"),
        ("Á", "A"), ("À", "A"), ("Â", "A"), ("Ã", "A"), ("Ä", "A"),
        ("é", "e"), ("è", "e"), ("ê", "e"), ("ë", "e"),
        ("É", "E"), ("È", "E"), ("Ê", "E"), ("Ë", "E"),
        ("í", "i"), ("ì", "i"), ("î", "i"), ("ï", "i"),
        ("Í", "I"), ("Ì", "I"), ("Î", "I"), ("Ï", "I"),
        ("ó", "o"), ("ò", "o"), ("ô", "o"), ("õ", "o"), ("ö", "o"),
        ("Ó", "O"), ("Ò", "O"), ("Ô", "O"), ("Õ", "O"), ("Ö", "O"),
        ("ú", "u"), ("ù", "u"), ("û", "u"), ("ü", "u"),
        ("Ú", "U"), ("Ù", "U"), ("Û", "U"), ("Ü", "U"),
        ("ç", "c"), ("Ç", "C"), ("ñ", "n"), ("Ñ", "N"),
        ("̀", ""), ("́", ""), ("̂", ""), ("̃", ""), ("̈", ""), ("̧", ""),
    ];

    private static readonly Dictionary<char, string> Map = Pairs.ToDictionary(p => p.From[0], p => p.To);

    /// <summary>The text with every mapped accented letter replaced by its plain letter and combining marks dropped.</summary>
    public static string RemoveAccents(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (Map.TryGetValue(ch, out var plain)) sb.Append(plain);
            else sb.Append(ch);
        }

        return sb.ToString();
    }
}
