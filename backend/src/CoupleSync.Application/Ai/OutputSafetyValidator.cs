using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoupleSync.Application.Ai;

/// <param name="Reason">A short code of the rule that failed (never the text itself: it may go to a log).</param>
public sealed record LlmValidationResult(bool IsValid, string? Reason)
{
    public static readonly LlmValidationResult Valid = new(true, null);

    public static LlmValidationResult Invalid(string reason) => new(false, reason);
}

/// <summary>
/// Every text written by a model goes through this before it is stored, shown or e-mailed (design 2.7): no links,
/// no contacts, no calls to act outside the app, and no merchant of the fact pack that the item does not cite.
/// The text reaches people under the app's name, so a prompt injection must not be able to carry any of these.
/// </summary>
public static partial class OutputSafetyValidator
{
    [GeneratedRegex(@"https?://|www\.|\b[\w-]+\.(?:com|net|org|br|io|app|me|ly|xyz|site|online|info)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"\S+@\S+\.\S+|(?<![\w@])@\w+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailOrHandle();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    // Phrases, not single words: "a conta de telefone subiu" and "pago por boleto" are ordinary sentences.
    // Written without accents and in lower case: they are matched against the folded text.
    [GeneratedRegex(
        @"\b(?:whatsapp|ligue para|liga para|telefone para contato|entre em contato|clique|acesse o site|acesse o link|no link|este link|senha|codigo de verificacao|chave pix|pix para|deposite)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ContactOrAction();

    /// <param name="pack">The fact pack sent with the request, when there is one.</param>
    /// <param name="refs">The ids of the pack that the item being validated cites.</param>
    public static LlmValidationResult Validate(string? text, JsonElement? pack = null, IReadOnlyCollection<string>? refs = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return LlmValidationResult.Valid;

        var folded = PromptText.Fold(text);
        if (Link().IsMatch(folded)) return LlmValidationResult.Invalid("LINK");
        if (EmailOrHandle().IsMatch(folded)) return LlmValidationResult.Invalid("EMAIL_OR_HANDLE");
        if (Uuid().IsMatch(folded)) return LlmValidationResult.Invalid("RANDOM_KEY");
        // Documents and phones by the pattern of the number, in the same spellings the privacy filter removes.
        if (ContactPatterns.HasDocument(folded)) return LlmValidationResult.Invalid("DOCUMENT");
        if (ContactPatterns.HasPhone(folded)) return LlmValidationResult.Invalid("PHONE");
        if (ContactOrAction().IsMatch(folded)) return LlmValidationResult.Invalid("CONTACT_OR_ACTION");

        if (pack is { } facts)
        {
            var cited = refs ?? [];
            foreach (var group in PackNames.Of(facts).GroupBy(n => n.Name, StringComparer.Ordinal))
            {
                if (group.Any(n => cited.Contains(n.Id))) continue;
                if (PackNames.Mentions(folded, group.Key)) return LlmValidationResult.Invalid("MERCHANT_NOT_IN_REFS");
            }
        }

        return LlmValidationResult.Valid;
    }
}

/// <summary>The named entities of a fact pack (design 3.9): every object with an "id" and a name in "m".</summary>
internal static class PackNames
{
    // Fixed labels of the pack, not names of places.
    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal) { "transferencia_pessoa", "outros" };

    /// <summary>(id, folded name) of every named entity of the pack.</summary>
    public static List<(string Id, string Name)> Of(JsonElement pack)
    {
        var names = new List<(string, string)>();
        Collect(pack, names);
        return names;
    }

    public static bool Mentions(string foldedText, string foldedName)
        => Regex.IsMatch(foldedText, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(foldedName)}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant);

    public static string Remove(string foldedText, string foldedName)
        => Regex.Replace(foldedText, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(foldedName)}(?![\p{{L}}\p{{N}}])", " ", RegexOptions.CultureInvariant);

    private static void Collect(JsonElement element, List<(string, string)> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("m", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var folded = PromptText.Fold(name.GetString()!).Trim();
                    if (folded.Length > 0 && !Labels.Contains(folded)) names.Add((id.GetString()!, folded));
                }

                foreach (var property in element.EnumerateObject()) Collect(property.Value, names);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, names);
                break;
        }
    }
}
