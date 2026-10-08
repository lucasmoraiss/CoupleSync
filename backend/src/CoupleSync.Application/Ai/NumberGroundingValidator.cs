using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoupleSync.Application.Ai;

/// <summary>
/// "The number comes from the code; the AI explains": every number and date in a text of the model must exist in the
/// fact pack the model received (design 2.7, rules a–f). The class and its cases are ready in phase 1; the Assistant
/// only uses it from phase 4 on, when there is a pack to compare with.
/// </summary>
public static partial class NumberGroundingValidator
{
    private static readonly decimal[] AlwaysAllowed = [0m, 1m, 2m, 100m];

    // (c) "três" to "vinte" are counts to check; um/uma, dois/duas, "o dobro" and "metade" pass.
    private static readonly Dictionary<string, int> CountWords = new(StringComparer.Ordinal)
    {
        ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["sete"] = 7, ["oito"] = 8, ["nove"] = 9, ["dez"] = 10,
        ["onze"] = 11, ["doze"] = 12, ["treze"] = 13, ["quatorze"] = 14, ["catorze"] = 14, ["quinze"] = 15,
        ["dezesseis"] = 16, ["dezessete"] = 17, ["dezoito"] = 18, ["dezenove"] = 19, ["vinte"] = 20,
    };

    [GeneratedRegex(@"\{\{[^{}]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Markers();

    [GeneratedRegex(@"(?<![\d/])(\d{1,2})/(\d{1,2})(?:/(\d{4}))?(?![\d/])", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"(?<![\d/])(\d{1,2})/(\d{4})(?![\d/])", RegexOptions.CultureInvariant)]
    private static partial Regex MonthYear();

    [GeneratedRegex(@"\b(\d{4})-(\d{2})(?:-(\d{2}))?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(
        @"(?<approx>(?:cerca de|quase|aproximadamente)\s+)?(?<cur>r\$\s*)?(?<num>\d{1,3}(?:\.\d{3})+(?:,\d+)?|\d+(?:,\d+)?)(?<pct>\s*%)?(?:\s*(?<scale>mil|milhao|milhoes)\b)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    /// <param name="text">The text of the model (one item, or the Assistant's answer).</param>
    /// <param name="pack">The fact pack that went with the request.</param>
    /// <param name="refs">The ids of the pack that the item cites; their names may appear in the text.</param>
    /// <param name="conversation">In the Assistant: the question and the history messages that were sent.</param>
    public static LlmValidationResult Validate(
        string? text,
        JsonElement pack,
        IReadOnlyCollection<string> refs,
        IReadOnlyCollection<string>? conversation = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return LlmValidationResult.Valid;

        // (a) names cited in refs and markers are not numbers of the text ("Posto 24h", "{{g1}}").
        var folded = Markers().Replace(PromptText.Fold(text), " ");
        foreach (var (_, name) in PackNames.Of(pack).Where(n => refs.Contains(n.Id)).OrderByDescending(n => n.Name.Length))
            folded = PackNames.Remove(folded, name);

        var facts = new Facts();
        CollectFacts(pack, facts);
        foreach (var message in conversation ?? [])
            foreach (var number in ExtractNumbers(DayMonth().Replace(MonthYear().Replace(PromptText.Fold(message), " "), " ")))
                facts.Numbers.Add(number.Value);

        // (d) dates first: what is left is numbers.
        var datesOk = true;
        folded = DayMonth().Replace(folded, match =>
        {
            var (day, month) = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            var known = match.Groups[3].Success
                ? facts.Days.Contains((int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), month, day))
                : facts.Days.Any(d => d.Month == month && d.Day == day);
            datesOk &= known;
            return " ";
        });
        folded = MonthYear().Replace(folded, match =>
        {
            datesOk &= facts.Months.Contains((int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
            return " ";
        });
        if (!datesOk) return LlmValidationResult.Invalid("DATE_NOT_IN_FACTS");

        // (b), (e), (f) numbers.
        foreach (var number in ExtractNumbers(folded))
        {
            if (number.IsBareInteger && facts.Years.Contains((int)number.Value)) continue;
            if (!facts.Allows(number.Value, number.Approximate)) return LlmValidationResult.Invalid("NUMBER_NOT_IN_FACTS");
        }

        // (c) counts written in words.
        foreach (Match word in Word().Matches(folded))
        {
            if (CountWords.TryGetValue(word.Value, out var count) && !facts.Allows(count, approximate: false))
                return LlmValidationResult.Invalid("COUNT_NOT_IN_FACTS");
        }

        return LlmValidationResult.Valid;
    }

    private readonly record struct Extracted(decimal Value, bool Approximate, bool IsBareInteger);

    /// <summary>(b) Brazilian numbers ("R$ 1.234,56", "12,5%", "1.234", "3") and "1,2 mil" / "2 milhões".</summary>
    private static IEnumerable<Extracted> ExtractNumbers(string folded)
    {
        foreach (Match match in Number().Matches(folded))
        {
            var raw = match.Groups["num"].Value.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
            if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) continue;

            var scale = match.Groups["scale"].Value;
            if (scale == "mil") value *= 1_000m;
            else if (scale.Length > 0) value *= 1_000_000m;

            var approximate = scale.Length > 0 || match.Groups["approx"].Success;
            var bare = scale.Length == 0 && !match.Groups["cur"].Success && !match.Groups["pct"].Success
                       && !match.Groups["approx"].Success && !raw.Contains('.');
            yield return new Extracted(value, approximate, bare);
        }
    }

    /// <summary>(e) the numeric leaves of the pack, and (d) its dates.</summary>
    private static void CollectFacts(JsonElement element, Facts facts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectFacts(property.Value, facts);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectFacts(item, facts);
                break;
            case JsonValueKind.Number when element.TryGetDecimal(out var number):
                facts.Numbers.Add(number);
                break;
            case JsonValueKind.String:
                foreach (Match date in IsoDate().Matches(element.GetString()!))
                {
                    var year = int.Parse(date.Groups[1].Value, CultureInfo.InvariantCulture);
                    var month = int.Parse(date.Groups[2].Value, CultureInfo.InvariantCulture);
                    facts.Years.Add(year);
                    facts.Months.Add((year, month));
                    if (date.Groups[3].Success) facts.Days.Add((year, month, int.Parse(date.Groups[3].Value, CultureInfo.InvariantCulture)));
                }

                break;
        }
    }

    private sealed class Facts
    {
        public List<decimal> Numbers { get; } = new();

        public HashSet<int> Years { get; } = new();

        public HashSet<(int Year, int Month)> Months { get; } = new();

        public HashSet<(int Year, int Month, int Day)> Days { get; } = new();

        /// <summary>
        /// (e) the fact, its absolute value and its rounding to 0 and 1 decimals; always 0, 1, 2 and 100.
        /// (f) within R$ 0.01 or 0.5% of the fact; 5% when the text says "mil", "milhões", "cerca de", "quase"...
        /// </summary>
        public bool Allows(decimal value, bool approximate)
        {
            foreach (var fact in Numbers.Concat(AlwaysAllowed))
            {
                foreach (var candidate in new[]
                         {
                             fact,
                             Math.Abs(fact),
                             Math.Round(Math.Abs(fact), 0, MidpointRounding.AwayFromZero),
                             Math.Round(Math.Abs(fact), 1, MidpointRounding.AwayFromZero),
                         })
                {
                    var difference = Math.Abs(value - candidate);
                    if (difference <= 0.01m) return true;
                    if (difference <= Math.Abs(fact) * (approximate ? 0.05m : 0.005m)) return true;
                }
            }

            return false;
        }
    }
}
