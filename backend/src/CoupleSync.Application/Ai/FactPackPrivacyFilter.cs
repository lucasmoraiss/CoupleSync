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

    private static readonly HashSet<string> NameParticles = new(StringComparer.Ordinal) { "de", "da", "do", "das", "dos", "e", "di", "du" };

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"[\p{L}\p{M}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\{\{([A-Z])\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PersonMarker();

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
            var person = people.FirstOrDefault(p => p.Marker == match.Groups[1].Value);
            var firstName = person?.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(firstName) ? "alguém do grupo" : firstName;
        });

    /// <summary>True when the text names a person marker ({{B}}...) that belongs to nobody in the group.</summary>
    public static bool MentionsUnknownPerson(string text, IReadOnlyList<AiPerson> people)
        => PersonMarker().Matches(text).Any(match => people.All(p => p.Marker != match.Groups[1].Value));

    /// <summary>A, B, C... for members in the order given (oldest membership first).</summary>
    public static IReadOnlyList<AiPerson> AsPeople(IEnumerable<string> namesByJoinOrder)
        => namesByJoinOrder.Take(26).Select((name, index) => new AiPerson(((char)('A' + index)).ToString(), name)).ToList();

    private static string Marker(string letter) => "{{" + letter + "}}";

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
