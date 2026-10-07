using System.Globalization;

namespace CoupleSync.Domain.ValueObjects;

/// <summary>
/// Text form of an amount in reais for messages shown to users: <c>R$ 1.234,56</c>. Built by hand so the
/// result does not depend on the culture data available on the host (invariant-globalization images).
/// </summary>
public static class BrlFormat
{
    public static string Format(decimal amount)
    {
        var rounded = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var fixedPoint = rounded.ToString("F2", CultureInfo.InvariantCulture);
        var separator = fixedPoint.IndexOf('.');
        var integer = fixedPoint[..separator];
        var cents = fixedPoint[(separator + 1)..];

        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0) grouped.Append('.');
            grouped.Append(integer[i]);
        }

        var sign = amount < 0 && rounded != 0 ? "-" : string.Empty;
        return $"{sign}R$ {grouped},{cents}";
    }
}
