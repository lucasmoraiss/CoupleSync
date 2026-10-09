using System.Text;
using System.Text.RegularExpressions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Ai;

/// <summary>
/// What leaves the API towards a provider, field by field (design 3.8). This phase has the free-text part: the names
/// and surnames of the group's members become {{A}}/{{B}}, and documents and contacts become "[removido]". It is
/// applied to the Assistant's question, to its history and to the context sent with them. The field-by-field part
/// (merchants, transfers, goals, incomes) comes with the fact pack.
/// Goals: the app never sends a title by itself — the data cites a goal as {{g1}}, and a title the system put in an
/// earlier answer becomes its marker again when that answer comes back as history. What a person types in a
/// question is theirs and goes as typed, a word that happens to be the title of a goal included.
/// </summary>
public static partial class FactPackPrivacyFilter
{
    public const string Removed = "[removido]";

    /// <summary>What stands for a goal that has no marker in the data sent.</summary>
    public const string UnnamedGoal = "uma meta";

    private static readonly HashSet<string> NameParticles = new(StringComparer.Ordinal) { "de", "da", "do", "das", "dos", "e", "di", "du" };

    /// <summary>
    /// Words of almost every question about money (folded: lower case, no accents). A goal whose whole title is one
    /// of them gets no line in <see cref="FindGoalMentions"/>: the line would go with nearly every question and tell
    /// the model nothing. Typical names of goals ("carro", "casa", "viagem", "reserva") are not here on purpose.
    /// </summary>
    public static IReadOnlySet<string> CommonQuestionWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // What every question is about.
        "meta", "metas", "objetivo", "gasto", "gastos", "despesa", "despesas", "dinheiro", "conta", "contas",
        "valor", "total", "saldo", "renda", "orcamento", "mes", "ano", "hoje", "grupo",
        // How every question is asked.
        "quanto", "quanta", "qual", "que", "como", "com", "para", "por", "uma", "mais",
    };

    /// <summary>The shortest title that gets a line in <see cref="FindGoalMentions"/>, in characters that are not white space.</summary>
    public const int NotedTitleMinLength = 3;

    /// <summary>
    /// Right before a lower-case "g1" written without braces, these (folded) words say it is not a goal: a goal is
    /// "a meta", and "o g1", "no g1", "portal g1" is how a news site is cited.
    /// </summary>
    private static readonly HashSet<string> NotAGoalBefore = new(StringComparer.Ordinal) { "o", "do", "no", "ao", "pelo", "portal", "site", "jornal" };

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

    // The same marker (group 1), or what a model may write in its place without the braces: [g1] (group 2) or a
    // bare g1 (group 3) — lower case as it was sent, a word of its own, not the start of a number like "g1,5".
    [GeneratedRegex(
        @"\{{1,2}\s*[gG]\s*([0-9]+)\s*\}{1,2}|\[\s*g([0-9]+)\s*\]|(?<![\p{L}\p{N}_{])g([0-9]+)(?![\p{L}\p{N}_}])(?![.,][0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnyGoalMarker();

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
    /// Puts the titles of the goals back in a text of the model, when answering the app (the app never sends the
    /// titles by itself: a goal goes as {{g1}}, {{g2}}...). A marker of no goal becomes "uma meta". A marker the model
    /// wrote without the braces (g1, [g1]) is read too, when goals were sent: the title for a goal that was sent,
    /// "uma meta" for one that was not ("g3" in a group with two goals is a marker the model made up, and would
    /// reach the person raw). Ordinary text stays: "G20", "5g1", "g1,5"; a bare "g1" right after a word that says
    /// it is not a goal ("o g1", "portal g1": <see cref="NotAGoalBefore"/>); and everything without braces when no
    /// goal was sent, so no marker was.
    /// </summary>
    public static string RestoreGoalTitles(string text, IReadOnlyDictionary<string, string> titlesByMarker)
        => AnyGoalMarker().Replace(text, match =>
        {
            var braced = match.Groups[1].Success;
            var bare = match.Groups[3].Success;
            if (!braced && titlesByMarker.Count == 0) return match.Value;
            if (bare && NotAGoalBefore.Contains(WordBefore(text, match.Index))) return match.Value;

            var number = braced ? match.Groups[1].Value : bare ? match.Groups[3].Value : match.Groups[2].Value;
            var known = titlesByMarker.TryGetValue("g" + number.TrimStart('0'), out var title) && ShownTitle(title).Length > 0;
            return known ? $"\"{ShownTitle(title)}\"" : UnnamedGoal;
        });

    /// <summary>The word (letters only, folded) that ends right before <paramref name="index"/>, white space apart; empty when there is none.</summary>
    private static string WordBefore(string text, int index)
    {
        var end = index;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        return PromptText.Fold(text[start..end]);
    }

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
    /// The opposite of <see cref="RestoreGoalTitles"/>, for an answer of the model that the app sends back as
    /// history: only what the system itself put there is taken out again — the title exactly as an answer shows it,
    /// between quotes. The same word written by the model without quotes ("gastos com carro") is ordinary text, and
    /// so is anything else between quotes.
    /// </summary>
    /// <param name="titlesByMarker">The goals that are in the data sent: each shown title becomes its marker again.</param>
    /// <param name="otherTitles">
    /// Titles of goals of the group that are not in the data (archived, completed): with no marker to stand for
    /// them they become "uma meta".
    /// </param>
    public static string MaskShownGoalTitles(
        string? text,
        IReadOnlyDictionary<string, string> titlesByMarker,
        IEnumerable<string>? otherTitles = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains('"')) return text;

        var shown = new List<(string Shown, string Replacement)>();
        foreach (var (marker, title) in InMarkerOrder(titlesByMarker)) Add(title, Marker(marker));
        foreach (var title in otherTitles ?? []) Add(title, UnnamedGoal);

        var result = text;
        // The longest first: "Casa na praia" is never read as a piece of another title.
        foreach (var (quoted, replacement) in shown.OrderByDescending(s => s.Shown.Length))
            result = result.Replace(quoted, replacement, StringComparison.Ordinal);
        return result;

        void Add(string? title, string replacement)
        {
            var quoted = $"\"{ShownTitle(title)}\"";
            // Two goals with the same title: the first one (an active goal before an archived one) stands for both.
            if (quoted.Length > 2 && shown.All(s => s.Shown != quoted)) shown.Add((quoted, replacement));
        }
    }

    /// <summary>A piece of a question that is also the title of goals in the data: the text as sent, and their markers.</summary>
    public sealed record GoalMention(string Text, IReadOnlyList<string> Markers);

    /// <summary>
    /// Where a question cites the title of a goal that is in the data — compared without accents or case, with any
    /// white space between the words of the title, whole words only, the longest title first. The question is not
    /// changed: who calls tells the model, in a line of its own, that those words are also the name of a goal.
    /// One mention per title, in the order they appear. A title that would be found in almost every question is
    /// not looked for (<see cref="IsNoiseAsATitle"/>): shorter than <see cref="NotedTitleMinLength"/>, one of
    /// <see cref="CommonQuestionWords"/>, or nothing but the name of members of the group.
    /// </summary>
    /// <param name="text">The question as it is sent (after the privacy filter and the hygiene).</param>
    /// <param name="titlesByMarker">Marker to title, each title as it would be sent (after the same privacy filter).</param>
    public static IReadOnlyList<GoalMention> FindGoalMentions(string? text, IReadOnlyDictionary<string, string> titlesByMarker)
    {
        if (string.IsNullOrEmpty(text) || titlesByMarker.Count == 0) return [];

        var titles = InMarkerOrder(titlesByMarker)
            // A title with a document or a contact in it would be compared with "[removido]": any removed piece would match.
            .Where(pair => !pair.Value.Contains(Removed, StringComparison.Ordinal))
            .Select(pair => (Marker: pair.Key, Words: TitleWords(pair.Value)))
            .Where(title => title.Words.Length > 0 && !IsNoiseAsATitle(title.Words))
            .GroupBy(title => string.Join(' ', title.Words), StringComparer.Ordinal)
            .Select(group => (Words: group.First().Words, Markers: group.Select(title => Marker(title.Marker)).ToList()))
            .OrderByDescending(title => title.Words.Sum(word => word.Length) + title.Words.Length)
            .ToList();
        if (titles.Count == 0) return [];

        // Folded once, character by character, so each folded position knows where it came from.
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
        var foldedText = folded.ToString();

        var taken = new List<(int Start, int End)>();
        var mentions = new List<(int Start, GoalMention Mention)>();
        foreach (var (words, markers) in titles)
        {
            var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", words.Select(Regex.Escape)) + @"(?![\p{L}\p{N}])";
            var first = true;
            foreach (Match match in Regex.Matches(foldedText, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                if (match.Length == 0) continue;
                // Up to where the next folded character came from: a combining accent (folded to nothing) goes with its letter.
                var (start, end) = (origin[match.Index], origin[match.Index + match.Length]);
                // Inside a longer title already found ("casa" in "casa na praia"): it is that other goal.
                if (taken.Any(span => start < span.End && span.Start < end)) continue;
                taken.Add((start, end));
                if (first) mentions.Add((start, new GoalMention(text[start..end], markers)));
                first = false;
            }
        }

        return mentions.OrderBy(mention => mention.Start).Select(mention => mention.Mention).ToList();
    }

    /// <summary>
    /// A title (its folded words, after the privacy filter) that almost any question would cite without meaning the
    /// goal: one or two characters; a single word of <see cref="CommonQuestionWords"/>; or only the name of members
    /// of the group — which the filter turned into their markers, with the particles of a name between them
    /// ("{{a}}", "{{b}} da {{b}}").
    /// </summary>
    private static bool IsNoiseAsATitle(string[] words)
    {
        if (words.Sum(word => word.Length) < NotedTitleMinLength) return true;
        if (words.Length == 1 && CommonQuestionWords.Contains(words[0])) return true;
        return words.Any(IsFoldedPersonMarker) && words.All(word => IsFoldedPersonMarker(word) || NameParticles.Contains(word));
    }

    /// <summary>"{{a}}": the marker of a person as the filter writes it, folded with the rest of the title.</summary>
    private static bool IsFoldedPersonMarker(string word)
        => word.Length == 5 && word.StartsWith("{{", StringComparison.Ordinal) && word.EndsWith("}}", StringComparison.Ordinal) && char.IsAsciiLetterLower(word[2]);

    /// <summary>g1, g2... g10: by number, not by text.</summary>
    private static IEnumerable<KeyValuePair<string, string>> InMarkerOrder(IReadOnlyDictionary<string, string> titlesByMarker)
        => titlesByMarker.OrderBy(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal);

    /// <summary>The words of a title as they are compared: folded, without the double quotes an answer never shows.</summary>
    private static string[] TitleWords(string? title)
        => PromptText.Fold(ShownTitle(title)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The title as an answer shows it: trimmed and without double quotes (the answer puts its own around it).</summary>
    private static string ShownTitle(string? title) => (title ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>A, B, C... for members in the order given (oldest membership first).</summary>
    public static IReadOnlyList<AiPerson> AsPeople(IEnumerable<string> namesByJoinOrder)
        => namesByJoinOrder.Take(26).Select((name, index) => new AiPerson(((char)('A' + index)).ToString(), name)).ToList();

    private static string Marker(string letter) => "{{" + letter + "}}";

    private static string MarkerLetter(Match match) => match.Groups[1].Value.ToUpperInvariant();

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
