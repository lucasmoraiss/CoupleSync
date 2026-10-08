using System.Globalization;
using System.Text.RegularExpressions;

namespace CoupleSync.Application.AppUpdate;

/// <summary>App versions as the app compares them: <c>X.Y.Z</c>, without the <c>v</c> of the tag nor a suffix (<c>-pit</c>).</summary>
public static class AppVersionNumber
{
    // ASCII digits only ([0-9], not \d), at most 9 per part so that each one fits an int.
    private static readonly Regex Pattern = new(
        @"^[vV]?([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(?:[-+].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));

    /// <summary><c>v1.1.0</c>, <c>1.1.0-pit</c> → <c>1.1.0</c>; anything that is not a version → null.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        Match match;
        try
        {
            match = Pattern.Match(text.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success) return null;

        return string.Join('.', Enumerable.Range(1, 3).Select(group =>
            int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture)));
    }
}
