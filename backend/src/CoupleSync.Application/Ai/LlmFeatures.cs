using CoupleSync.Application.Common.Options;

namespace CoupleSync.Application.Ai;

/// <summary>What a call is for. The feature decides the chain, the temperature and the budget the call counts in.</summary>
public static class LlmFeatures
{
    public const string Chat = "chat";
    public const string InsightDaily = "insight_daily";
    public const string InsightWeekly = "insight_weekly";
    public const string InsightMonthly = "insight_monthly";
    public const string Education = "education";
    public const string Categorize = "categorize";
    public const string Ocr = "ocr";

    /// <summary>
    /// The weekly and monthly summaries: outside the group's budget, under the jobs' own daily ceiling, so that the
    /// 06:17 run never eats the Assistant's quota.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Jobs = [InsightWeekly, InsightMonthly];

    /// <summary>Interactive use: counts in the group's budget and in the global interactive ceiling.</summary>
    public static readonly IReadOnlyCollection<string> Interactive = [Chat, InsightDaily, Education, Categorize, Ocr];

    public static bool IsJob(string feature) => Jobs.Contains(feature);

    /// <summary>The chain of a feature. A feature without a chain is a programming error (OCR has none before its phase).</summary>
    public static string ChainOf(string feature) => feature switch
    {
        Chat => AiChains.Assistant,
        InsightDaily => AiChains.Daily,
        InsightWeekly or InsightMonthly => AiChains.Weekly,
        Education => AiChains.Education,
        Categorize => AiChains.Categorize,
        _ => throw new ArgumentException($"There is no chain for the feature '{feature}'.", nameof(feature)),
    };

    /// <summary>0 for extraction and categorization; 0.2 for insights, the guide and the Assistant.</summary>
    public static decimal TemperatureOf(string feature) => feature is Categorize or Ocr ? 0m : 0.2m;
}

/// <summary>
/// The providers the current AI consent text covers. It stays in code, next to the consent version: a provider
/// configured before the text names it (and before the group accepts again) receives nothing — the gateway ignores
/// its links. Version 2 of the text: Google (Gemini) and Groq.
/// Adding or changing a provider here goes together with a new <c>AiConsent.CurrentVersion</c>, in the same deploy
/// in which the provider starts to count: nobody may have accepted a text that did not name it. Taking a key away
/// needs nothing.
/// </summary>
public static class AiConsentCoverage
{
    public static readonly IReadOnlyList<string> Providers = [AiOptions.GeminiProviderName, AiOptions.GroqProviderName];

    public static bool Covers(string provider) => Providers.Contains(provider, StringComparer.OrdinalIgnoreCase);
}
