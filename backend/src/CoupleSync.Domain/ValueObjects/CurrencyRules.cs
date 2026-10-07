namespace CoupleSync.Domain.ValueObjects;

/// <summary>Only Brazilian reais are accepted; an absent currency means BRL.</summary>
public static class CurrencyRules
{
    public const string Brl = "BRL";

    public const string InvalidMessage = "Só é aceita a moeda BRL (real).";

    /// <summary>True when the currency is absent/blank (assumed BRL) or is BRL in any case.</summary>
    public static bool IsAccepted(string? currency) => TryNormalize(currency) is not null;

    /// <summary>BRL for an absent/blank value or "brl"/" BRL "; null for any other currency.</summary>
    public static string? TryNormalize(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return Brl;
        return string.Equals(currency.Trim(), Brl, StringComparison.OrdinalIgnoreCase) ? Brl : null;
    }

    /// <summary>Stored form of a currency already validated at the edge.</summary>
    public static string NormalizeOrBrl(string? currency) => TryNormalize(currency) ?? Brl;

    /// <summary>True for stored values that count in sums in reais (tolerates legacy case/spacing).</summary>
    public static bool IsBrl(string? currency) =>
        !string.IsNullOrWhiteSpace(currency) && string.Equals(currency.Trim(), Brl, StringComparison.OrdinalIgnoreCase);
}
