using System.Text.RegularExpressions;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

internal static class BrazilianCurrencyParser
{
    // Strips R$, +, leading/trailing whitespace then converts "1.234,56" → 1234.56
    private static readonly Regex CleanPattern = new(@"[R$\s+]", RegexOptions.Compiled);

    /// <summary>
    /// Parses a Brazilian currency string such as "R$ 1.234,56", "-R$ 1.234,56", or "1.234,56".
    /// Returns a positive value; use <paramref name="isNegative"/> to apply sign externally.
    /// </summary>
    public static bool TryParse(string raw, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var clean = CleanPattern.Replace(raw, "").Trim();

        // Determine sign from original string
        bool negative = raw.Contains('-');

        // Remove any remaining minus
        clean = clean.Replace("-", "").Trim();

        // Brazilian format: dots as thousand separators, comma as decimal separator
        // "1.234,56" → "1234.56"
        clean = clean.Replace(".", "").Replace(",", ".");

        if (!decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out amount))
            return false;

        if (negative)
            amount = -amount;

        return true;
    }

    /// <summary>
    /// Parses and returns absolute (positive) value regardless of sign in the string.
    /// </summary>
    public static bool TryParseAbsolute(string raw, out decimal amount)
    {
        if (!TryParse(raw, out amount))
            return false;
        amount = Math.Abs(amount);
        return true;
    }
}
