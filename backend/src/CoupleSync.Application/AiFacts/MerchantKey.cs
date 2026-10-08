using System.Text;
using System.Text.RegularExpressions;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.AiFacts;

/// <summary>
/// The key of an establishment (design 3.3, item 1): what "NETFLIX.COM 866-579" and "Netflix.com" have in common.
/// Lower case by the invariant culture and accents by the explicit table (<see cref="AccentFolding"/>), never by
/// Unicode normalization: the answer is the same in a host without ICU.
/// </summary>
public static partial class MerchantKey
{
    /// <summary>The fixed label of a transfer to a person (design 3.8). It never forms a recurrence.</summary>
    public const string PersonTransfer = "transferencia_pessoa";

    /// <summary>What acquirers and wallets write before the name of the shop. Locked by a test.</summary>
    public static IReadOnlyList<string> AcquirerPrefixes { get; } =
        ["pg *", "pag*", "mp *", "mercadopago*", "pagseguro", "picpay*", "ifd*", "ec *"];

    /// <summary>Countries, as statements write them at the end of the line. Locked by a test.</summary>
    public static IReadOnlyList<string> CountrySuffixes { get; } = ["br", "bra", "brasil", "brazil"];

    /// <summary>The 27 federative units. Locked by a test.</summary>
    public static IReadOnlyList<string> StateSuffixes { get; } =
    [
        "ac", "al", "am", "ap", "ba", "ce", "df", "es", "go", "ma", "mg", "ms", "mt", "pa", "pb", "pe", "pi", "pr",
        "rj", "rn", "ro", "rr", "rs", "sc", "se", "sp", "to",
    ];

    /// <summary>Cities that statements write after the name of the shop (already without accents). Locked by a test.</summary>
    public static IReadOnlyList<string> CitySuffixes { get; } =
    [
        "sao paulo", "rio de janeiro", "belo horizonte", "brasilia", "salvador", "fortaleza", "curitiba", "manaus",
        "recife", "porto alegre", "goiania", "belem", "guarulhos", "campinas", "sao luis", "maceio", "natal",
        "campo grande", "teresina", "joao pessoa", "osasco", "barueri", "santo andre", "sao bernardo do campo",
        "ribeirao preto", "sorocaba", "uberlandia", "florianopolis", "vitoria", "cuiaba", "aracaju", "niteroi",
        "santos", "sao jose dos campos", "londrina", "joinville",
    ];

    private static readonly string[] CompactPrefixes =
        AcquirerPrefixes.Select(p => p.Replace(" ", string.Empty, StringComparison.Ordinal)).OrderByDescending(p => p.Length).ToArray();

    private static readonly HashSet<string> Countries = CountrySuffixes.ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> States = StateSuffixes.ToHashSet(StringComparer.Ordinal);
    private static readonly string[][] Cities = CitySuffixes.Select(c => c.Split(' ')).OrderByDescending(c => c.Length).ToArray();

    /// <summary>The mark of an instalment, "03/10": part 3 of 10 (design 3.4).</summary>
    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2})(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex InstallmentMark();

    [GeneratedRegex(@"\bparc(ela)?\.?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingInstallmentWord();

    [GeneratedRegex(@"\s*\*\s*", RegexOptions.CultureInvariant)]
    private static partial Regex AsteriskWithSpaces();

    [GeneratedRegex(@"\.com(\.br)?(?![a-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DotCom();

    [GeneratedRegex(@"\d{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigits();

    /// <summary>Lower case, without accents: the form every rule of this context compares text in.</summary>
    public static string Fold(string value)
        => AccentFolding.RemoveAccents(value).ToLowerInvariant();

    /// <summary>The key of the establishment; empty when nothing useful is left of the text.</summary>
    public static string Normalize(string? merchant)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return string.Empty;

        var text = AsteriskWithSpaces().Replace(Fold(merchant).Trim(), "*");
        text = RemovePrefixes(text).Replace('*', ' ');
        text = RemoveInstallmentMark(text);
        text = DotCom().Replace(text, " ");
        text = LongDigits().Replace(text, " ");

        var tokens = Tokens(text);
        RemoveLocationSuffix(tokens);
        return string.Join(' ', tokens);
    }

    /// <summary>The text without the "03/10" (and the word "parc" before it), as the name shown for an instalment.</summary>
    public static string RemoveInstallmentMark(string text)
    {
        var match = InstallmentMark().Match(text);
        if (!match.Success) return text;
        var before = TrailingInstallmentWord().Replace(text[..match.Index], string.Empty);
        return before + " " + text[(match.Index + match.Length)..];
    }

    private static string RemovePrefixes(string text)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in CompactPrefixes)
            {
                if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                // "pagseguro" has no asterisk of its own: it only counts as a prefix when the name of the shop follows.
                var rest = text[prefix.Length..].TrimStart(' ', '*');
                if (rest.Length == 0) continue;
                if (!prefix.EndsWith('*') && char.IsLetterOrDigit(text[prefix.Length])) continue;
                text = rest;
                changed = true;
                break;
            }
        }

        return text;
    }

    /// <summary>The words of a folded text: letters and digits only, everything else separates.</summary>
    public static List<string> Tokens(string folded)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in folded)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Country, then state, then city, from the end; never the whole name.</summary>
    private static void RemoveLocationSuffix(List<string> tokens)
    {
        if (tokens.Count > 1 && Countries.Contains(tokens[^1])) tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count > 1 && States.Contains(tokens[^1])) tokens.RemoveAt(tokens.Count - 1);
        foreach (var city in Cities)
        {
            if (tokens.Count <= city.Length) continue;
            var matches = true;
            for (var i = 0; i < city.Length && matches; i++)
                matches = tokens[tokens.Count - city.Length + i] == city[i];
            if (!matches) continue;
            tokens.RemoveRange(tokens.Count - city.Length, city.Length);
            break;
        }
    }

    /// <summary>True when the key has the hint as whole words ("google play" in "google play youtube").</summary>
    public static bool ContainsWords(string key, string hint)
    {
        if (key.Length < hint.Length) return false;
        var index = key.IndexOf(hint, StringComparison.Ordinal);
        while (index >= 0)
        {
            var startsWord = index == 0 || key[index - 1] == ' ';
            var end = index + hint.Length;
            var endsWord = end == key.Length || key[end] == ' ';
            if (startsWord && endsWord) return true;
            index = key.IndexOf(hint, index + 1, StringComparison.Ordinal);
        }

        return false;
    }
}

/// <summary>
/// Static lists that say what an establishment is (design 3.3, items 5 and 9). Written as keys
/// (<see cref="MerchantKey.Normalize"/>) and compared by whole words. Locked by a test.
/// </summary>
public static class MerchantHints
{
    /// <summary>Streaming, music, apps, cloud and app shops.</summary>
    public static IReadOnlyList<string> SubscriptionMerchantHints { get; } =
    [
        "netflix", "spotify", "disney", "hbo max", "prime video", "amazon prime", "globoplay", "deezer", "youtube",
        "apple bill", "google play", "google one", "google storage", "icloud", "microsoft", "adobe", "openai", "chatgpt",
        "paramount", "crunchyroll", "telecine", "premiere", "dropbox", "canva", "linkedin", "duolingo", "tidal",
        "twitch", "xbox", "playstation", "nintendo", "kindle", "audible", "github", "notion",
    ];

    /// <summary>Hints that are also common words: they only count when they are the whole key.</summary>
    public static IReadOnlyList<string> SubscriptionExactHints { get; } = ["max"];

    /// <summary>Electricity, water, gas, internet and telephone companies, and the words people write for them.</summary>
    public static IReadOnlyList<string> UtilityMerchantHints { get; } =
    [
        "enel", "cemig", "copel", "cpfl", "light", "neoenergia", "equatorial", "energisa", "celesc", "coelba", "celpe",
        "sabesp", "cedae", "copasa", "sanepar", "embasa", "caesb", "corsan", "comgas", "naturgy", "ultragaz",
        "supergasbras", "liquigas", "vivo", "claro", "tim", "oi", "sky", "algar", "brisanet",
        "luz", "energia", "agua", "gas", "internet", "telefone", "celular", "condominio", "aluguel",
    ];

    public static bool IsSubscription(string key)
        => SubscriptionExactHints.Contains(key) || SubscriptionMerchantHints.Any(h => MerchantKey.ContainsWords(key, h));

    public static bool IsUtility(string key)
        => UtilityMerchantHints.Any(h => MerchantKey.ContainsWords(key, h));
}

/// <summary>
/// The rule of the transfer to a person (design 3.8): the transaction keeps counting in the totals, but its text
/// never forms a recurrence nor leaves the API. The description is only ever read here, on the server.
/// </summary>
public static class PersonTransferRule
{
    /// <summary>Words that say "this is a transfer", compared as whole words of the folded text. Locked by a test.</summary>
    public static IReadOnlyList<string> Markers { get; } =
        ["pix", "ted", "doc", "transferencia", "transf", "enviado", "recebido", "para"];

    /// <summary>Common first names and surnames in Brazil, without accents. Locked by a test.</summary>
    public static IReadOnlyList<string> CommonPersonNames { get; } =
    [
        "maria", "jose", "joao", "ana", "antonio", "francisco", "carlos", "paulo", "pedro", "lucas", "luiz", "luis",
        "marcos", "gabriel", "rafael", "daniel", "marcelo", "bruno", "eduardo", "felipe", "raimundo", "rodrigo",
        "manoel", "manuel", "mateus", "matheus", "andre", "fernando", "fabio", "leonardo", "gustavo", "guilherme",
        "leandro", "tiago", "thiago", "anderson", "ricardo", "marcio", "jorge", "sebastiao", "alexandre", "roberto",
        "edson", "diego", "vitor", "victor", "sergio", "claudio", "julio", "cesar", "renato", "vinicius", "caio",
        "henrique", "igor", "samuel", "miguel", "arthur", "artur", "davi", "david", "bernardo", "heitor", "enzo",
        "francisca", "antonia", "adriana", "juliana", "marcia", "fernanda", "patricia", "aline", "sandra", "camila",
        "amanda", "bruna", "jessica", "leticia", "julia", "luciana", "vanessa", "mariana", "gabriela", "vera",
        "vitoria", "larissa", "claudia", "beatriz", "rita", "luana", "sonia", "renata", "eliane", "josefa", "simone",
        "natalia", "cristiane", "carla", "debora", "rosangela", "jaqueline", "rosa", "daniela", "aparecida", "marlene",
        "terezinha", "raimunda", "andreia", "fabiana", "lucia", "raquel", "angela", "rafaela", "joana", "luzia",
        "elaine", "daniele", "regina", "isabela", "isabel", "bianca", "helena", "alice", "laura", "sofia", "sophia",
        "silva", "santos", "oliveira", "souza", "sousa", "rodrigues", "ferreira", "alves", "pereira", "lima", "gomes",
        "costa", "ribeiro", "martins", "carvalho", "almeida", "lopes", "soares", "fernandes", "vieira", "barbosa",
        "rocha", "dias", "nascimento", "andrade", "moreira", "nunes", "marques", "machado", "mendes", "freitas",
        "cardoso", "ramos", "goncalves", "santana", "teixeira", "araujo", "melo", "barros", "pinto", "cavalcanti",
        "correia", "moraes", "morais", "monteiro", "batista", "campos", "castro", "borges", "medeiros", "azevedo",
        "duarte", "reis", "miranda", "pires", "farias", "cunha", "moura", "nogueira", "tavares", "macedo",
    ];

    private static readonly HashSet<string> MarkerSet = Markers.ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Names = CommonPersonNames.ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Connectives = new(StringComparer.Ordinal) { "de", "da", "do", "dos", "das", "e" };

    /// <summary>
    /// True when (a) the merchant or the description has a transfer marker, or (b) the establishment is made only of
    /// common person names and one-letter initials ("MARIA S SILVA").
    /// </summary>
    /// <param name="merchant">The establishment of the transaction.</param>
    /// <param name="description">The description of the transaction (never leaves the server).</param>
    /// <param name="establishment">The text used as establishment (the merchant, or the description when there is none).</param>
    public static bool IsPersonTransfer(string? merchant, string? description, string? establishment = null)
        => HasMarker(merchant) || HasMarker(description) || IsPersonName(establishment ?? merchant);

    private static bool HasMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = MerchantKey.Tokens(MerchantKey.Fold(text));
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!MarkerSet.Contains(tokens[i])) continue;
            // "para " only says something when a name follows it ("para joao").
            if (tokens[i] == "para" && i == tokens.Count - 1) continue;
            return true;
        }

        return false;
    }

    private static bool IsPersonName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var names = 0;
        foreach (var token in MerchantKey.Tokens(MerchantKey.Fold(text)))
        {
            if (token.All(char.IsDigit)) continue;
            if (Names.Contains(token)) names++;
            else if (token.Length != 1 && !Connectives.Contains(token)) return false;
        }

        return names > 0;
    }
}
