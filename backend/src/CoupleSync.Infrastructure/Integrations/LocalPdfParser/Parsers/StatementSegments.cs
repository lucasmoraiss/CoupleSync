using System.Text.RegularExpressions;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

internal static class StatementSegments
{
    /// <summary>
    /// Splits statement text into entries. An entry starts at a match of <paramref name="start"/> and
    /// ends at the next entry's start or at the end of its line, whichever comes first. PdfPig can hand a
    /// whole page over as one line, so the next entry may start right after the previous amount.
    /// </summary>
    public static IEnumerable<string> Split(string text, Regex start)
    {
        var starts = start.Matches(text);
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i].Index;
            var to = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var newline = text.IndexOf('\n', from);
            if (newline >= 0 && newline < to)
                to = newline;

            yield return text[from..to];
        }
    }

    /// <summary>Last match of <paramref name="money"/> in <paramref name="text"/> (null when there is none).</summary>
    public static Match? LastMatch(Regex money, string text)
    {
        Match? last = null;
        foreach (Match m in money.Matches(text))
            last = m;
        return last;
    }
}
