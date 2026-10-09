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

    /// <summary>Words that say the number next to them is a document or a contact, folded. Locked by a test.</summary>
    public static IReadOnlyList<string> DocumentOrContactWords { get; } =
    [
        "cpf", "cnpj", "rg", "doc", "documento", "tel", "telefone", "fone", "cel", "celular", "whatsapp", "whats",
        "zap", "contato", "fax", "pix", "chave",
    ];

    private static readonly HashSet<string> DocumentOrContactWordSet = DocumentOrContactWords.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when a line of a statement (or a note) carries what identifies a person or an account: an e-mail, a
    /// random Pix key, or a number that is a document or a phone. A number of 9 to 14 digits is one of those when
    /// (a) it is written with the punctuation or the spaces of a CPF, a CNPJ or a phone, or (b) it has the check
    /// digits of a CPF (11 digits) or of a CNPJ (14 digits), or (c) a word of
    /// <see cref="DocumentOrContactWords"/> is in the text. A bare number that is none of those (a client code, a
    /// contract) is not: <see cref="RemoveLongNumbers"/> takes it out of what is shown.
    /// <see cref="FilterFreeText"/>, which decides what goes to a provider, does not use this: there every such
    /// number is removed.
    /// </summary>
    public static bool HasDocumentOrContact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (Email().IsMatch(text) || Uuid().IsMatch(text)) return true;
        if (ContactPatterns.HasFormatted(text) || ContactPatterns.HasValidRawDocument(text)) return true;
        return ContactPatterns.HasAny(text)
               && Word().Matches(text).Any(word => DocumentOrContactWordSet.Contains(PromptText.Fold(word.Value)));
    }

    /// <summary>The text without its runs of 6 or more digits (the catch-all of <see cref="FilterFreeText"/>), each one replaced by a space.</summary>
    public static string RemoveLongNumbers(string text) => LongDigits().Replace(text, " ");

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
    /// wrote without the braces (g1, [g1]) is read too, but only when it is exactly the marker of a goal that was
    /// sent: "G20", "5g1" or the "g7" of a group with one goal are ordinary text and stay.
    /// </summary>
    public static string RestoreGoalTitles(string text, IReadOnlyDictionary<string, string> titlesByMarker)
        => AnyGoalMarker().Replace(text, match =>
        {
            var braced = match.Groups[1].Success;
            var number = braced ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            var known = titlesByMarker.TryGetValue("g" + number.TrimStart('0'), out var title) && ShownTitle(title).Length > 0;
            if (known) return $"\"{ShownTitle(title)}\"";
            return braced ? UnnamedGoal : match.Value;
        });

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
    /// One mention per title, in the order they appear.
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
            .Where(title => title.Words.Length > 0)
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
