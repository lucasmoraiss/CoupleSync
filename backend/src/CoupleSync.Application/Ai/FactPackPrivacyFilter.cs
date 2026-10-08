using System.Text;
using System.Text.RegularExpressions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Ai;

/// <summary>
/// What leaves the API towards a provider, field by field (design 3.8). This phase has the free-text part: the names
/// and surnames of the group's members become {{A}}/{{B}}, and documents and contacts become "[removido]". It is
/// applied to the Assistant's question, to its history and to the context sent with them. The field-by-field part
/// (merchants, transfers, goals, incomes) comes with the fact pack.
/// </summary>
public static partial class FactPackPrivacyFilter
{
    public const string Removed = "[removido]";

    /// <summary>What stands for a goal that has no marker in the data sent.</summary>
    public const string UnnamedGoal = "uma meta";

    [GeneratedRegex(@"""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    private static readonly HashSet<string> NameParticles = new(StringComparer.Ordinal) { "de", "da", "do", "das", "dos", "e", "di", "du" };

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"[\p{L}\p{M}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    // A model does not always write a marker exactly as asked ({{A}}, {{g1}}): case, inner spaces and one brace
    // instead of two are accepted when reading its answer. What is sent always has the exact form.
    [GeneratedRegex(@"\{{1,2}\s*([A-Za-z])\s*\}{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex PersonMarker();

    [GeneratedRegex(@"\{{1,2}\s*[gG]\s*([0-9]+)\s*\}{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex GoalMarker();

    /// <summary>The text without the members' names, documents, phones, e-mails, random Pix keys and long numbers.</summary>
    public static string FilterFreeText(string? text, IReadOnlyList<AiPerson> people)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Order matters: an e-mail or a key may contain a name or digits; documents and phones in every common
        // spelling (ContactPatterns) come before the catch-all for long runs of digits.
        var result = Email().Replace(text, Removed);
        result = Uuid().Replace(result, Removed);
        result = ContactPatterns.Replace(result, Removed);
        result = LongDigits().Replace(result, Removed);

        var markerOfName = NameTokens(people);
        if (markerOfName.Count == 0) return result;

        result = Word().Replace(result, word =>
            markerOfName.TryGetValue(PromptText.Fold(word.Value), out var marker) ? Marker(marker) : word.Value);

        // "Mariana Souza" became "{{A}} {{A}}": one person, one marker.
        foreach (var person in people)
        {
            var marker = Regex.Escape(Marker(person.Marker));
            result = Regex.Replace(result, $@"{marker}(?:\s+{marker})+", Marker(person.Marker), RegexOptions.CultureInvariant);
        }

        return result;
    }

    /// <summary>Puts the first names back in a text of the model, when answering the app.</summary>
    public static string RestoreNames(string text, IReadOnlyList<AiPerson> people)
        => PersonMarker().Replace(text, match =>
        {
            var person = people.FirstOrDefault(p => p.Marker == MarkerLetter(match));
            var firstName = person?.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(firstName) ? "alguém do grupo" : firstName;
        });

    /// <summary>
    /// Puts the titles of the goals back in a text of the model, when answering the app (the titles never leave the
    /// API: a goal goes as {{g1}}, {{g2}}...). A marker of no goal becomes "uma meta".
    /// </summary>
    public static string RestoreGoalTitles(string text, IReadOnlyDictionary<string, string> titlesByMarker)
        => GoalMarker().Replace(text, match =>
            titlesByMarker.TryGetValue(GoalKey(match), out var title) && ShownTitle(title).Length > 0
                ? $"\"{ShownTitle(title)}\""
                : UnnamedGoal);

    /// <summary>True when the text names a person marker ({{B}}...) that belongs to nobody in the group.</summary>
    public static bool MentionsUnknownPerson(string text, IReadOnlyList<AiPerson> people)
        => PersonMarker().Matches(text).Any(match => people.All(p => p.Marker != MarkerLetter(match)));

    /// <summary>
    /// True when, with every marker taken out, a brace is still there: a marker the model spelled in a way nobody
    /// reads ({{meta1}}, a marker cut in half). Such an answer is not shown: the raw marker would reach the person.
    /// </summary>
    public static bool HasMarkerLeftovers(string text)
    {
        var rest = GoalMarker().Replace(PersonMarker().Replace(text, string.Empty), string.Empty);
        return rest.Contains('{') || rest.Contains('}');
    }

    /// <summary>
    /// The opposite of <see cref="RestoreGoalTitles"/>, for what comes from the app: the answer the person read has
    /// the title of the goal and returns as history, and a question may name a goal. Every known title becomes its
    /// marker again before anything is sent — compared without accents or case, with any white space between its
    /// words, whole words only, the longest title first, with or without the quotes of the answer.
    /// </summary>
    /// <param name="titlesByMarker">The goals that are in the data sent: each title becomes its marker.</param>
    /// <param name="otherTitles">
    /// Titles of goals of the group that are not in the data (archived, completed): they do not leave either, and
    /// with no marker to stand for them they become "uma meta".
    /// </param>
    public static string ReplaceGoalTitles(
        string? text,
        IReadOnlyDictionary<string, string> titlesByMarker,
        IEnumerable<string>? otherTitles = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var titles = titlesByMarker
            .Select(pair => (Order: pair.Key, Replacement: Marker(pair.Key), Title: pair.Value))
            .Concat((otherTitles ?? []).Select(title => (Order: "~", Replacement: UnnamedGoal, Title: title)))
            .Select(title => (title.Order, title.Replacement, Words: TitleWords(title.Title)))
            .Where(title => title.Words.Length > 0)
            .OrderByDescending(title => title.Words.Sum(word => word.Length) + title.Words.Length)
            .ThenBy(title => title.Order, StringComparer.Ordinal)
            .ToList();

        var result = text;
        foreach (var (_, replacement, words) in titles)
        {
            // Quotes around the title (the answer shows it so) or around one of its words go with it.
            var pattern = @"(?<![\p{L}\p{N}])""?" + string.Join(@"""?\s+""?", words.Select(Regex.Escape)) + @"""?(?![\p{L}\p{N}])";
            result = ReplaceFolded(result, new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), replacement);
        }

        return result;
    }

    /// <summary>
    /// For an answer of the model that comes back as history: a title is shown between quotes, so whatever is still
    /// between quotes after <see cref="ReplaceGoalTitles"/> may be the title of a goal that was renamed or deleted
    /// since (nobody knows the old title any more). It does not leave: it becomes "uma meta".
    /// </summary>
    public static string ReplaceQuoted(string text) => Quoted().Replace(text, UnnamedGoal);

    /// <summary>The words of a title as they are compared: folded, without the double quotes an answer never shows.</summary>
    private static string[] TitleWords(string? title)
        => PromptText.Fold(ShownTitle(title)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The title as an answer shows it: trimmed and without double quotes (the answer puts its own around it).</summary>
    private static string ShownTitle(string? title) => (title ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>
    /// Replaces what <paramref name="pattern"/> finds in the folded text (lower case, no accents), in the text as it
    /// was written. Folding is done character by character, so each folded position knows where it came from.
    /// </summary>
    private static string ReplaceFolded(string text, Regex pattern, string replacement)
    {
        var folded = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            foreach (var ch in PromptText.Fold(text[i].ToString()))
            {
                folded.Append(ch);
                origin.Add(i);
            }
        }

        origin.Add(text.Length);

        var result = new StringBuilder(text.Length);
        var copied = 0;
        foreach (Match match in pattern.Matches(folded.ToString()))
        {
            if (match.Length == 0) continue;
            var start = origin[match.Index];
            // Up to where the next folded character came from: a combining accent (folded to nothing) goes with its letter.
            var end = origin[match.Index + match.Length];
            if (start < copied) continue;
            result.Append(text, copied, start - copied).Append(replacement);
            copied = end;
        }

        return result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>A, B, C... for members in the order given (oldest membership first).</summary>
    public static IReadOnlyList<AiPerson> AsPeople(IEnumerable<string> namesByJoinOrder)
        => namesByJoinOrder.Take(26).Select((name, index) => new AiPerson(((char)('A' + index)).ToString(), name)).ToList();

    private static string Marker(string letter) => "{{" + letter + "}}";

    private static string MarkerLetter(Match match) => match.Groups[1].Value.ToUpperInvariant();

    /// <summary>"g1" for {{g1}}, {{G1}}, {{ g 01 }}: the key of the titles in <see cref="RestoreGoalTitles"/>.</summary>
    private static string GoalKey(Match match) => "g" + match.Groups[1].Value.TrimStart('0');

    /// <summary>Every word of every member's name (folded), except particles, with the marker of its first owner.</summary>
    private static Dictionary<string, string> NameTokens(IReadOnlyList<AiPerson> people)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var person in people)
        {
            foreach (Match word in Word().Matches(person.FullName))
            {
                var folded = PromptText.Fold(word.Value);
                if (folded.Length < 2 || NameParticles.Contains(folded)) continue;
                tokens.TryAdd(folded, person.Marker);
            }
        }

        return tokens;
    }
}
