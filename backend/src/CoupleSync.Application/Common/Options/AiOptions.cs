namespace CoupleSync.Application.Common.Options;

/// <summary>A link of a chain, written in configuration as "provider|model".</summary>
public sealed record LlmLink(string Provider, string Model);

/// <summary>Limits of one model at its provider. Null = unknown: an unknown limit blocks nothing.</summary>
public sealed class AiLimit
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int? Rpd { get; set; }

    public long? Tpd { get; set; }

    public int? Rpm { get; set; }

    public long? Tpm { get; set; }
}

public static class AiChains
{
    public const string Assistant = "Assistant";
    public const string Weekly = "Weekly";
    public const string Daily = "Daily";
    public const string Categorize = "Categorize";
    public const string Education = "Education";

    public static readonly IReadOnlyList<string> All = [Assistant, Weekly, Daily, Categorize, Education];
}

/// <summary>
/// The "Ai" configuration section (design 2.4 and 2.5). The defaults here are the ones of the design; every value
/// can be replaced by an environment variable (Ai__Chains__Assistant__0=gemini|gemini-flash-latest, Ai__Limits__0__Rpm=5...).
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";
    public const string GeminiProviderName = "gemini";
    public const string FakeProviderName = "fake";

    private const string FlashLatest = "gemini-flash-latest";
    private const string FlashLiteLatest = "gemini-flash-lite-latest";
    private const string Flash3Preview = "gemini-3-flash-preview";

    /// <summary>Emergency switch: nothing is sent to any provider.</summary>
    public bool Disabled { get; set; }

    /// <summary>Tests and "App E2E" only: the fake provider becomes the single link of every chain.</summary>
    public bool UseFakeProvider { get; set; }

    /// <summary>Interactive calls of one group per Brasília day.</summary>
    public int GroupDailyCalls { get; set; } = 25;

    public long GroupDailyTokens { get; set; } = 60_000;

    /// <summary>Calls of the weekly and monthly jobs per Brasília day, all groups together.</summary>
    public int JobDailyCalls { get; set; } = 60;

    /// <summary>Interactive calls per Brasília day, all groups together: there is one key and its quota is unknown.</summary>
    public int GlobalDailyInteractiveCalls { get; set; } = 150;

    /// <summary>Total time of an interactive call (the app waits up to 30 s).</summary>
    public TimeSpan InteractiveBudget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Time of one link in an interactive call: the smaller of this and what is left of the total.</summary>
    public TimeSpan LinkTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan JobLinkTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The longest a job waits for a minute window before falling to the next link.</summary>
    public TimeSpan JobMaxWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Quality first where there are few calls; speed and quota first where there is volume (design 2.2).</summary>
    public Dictionary<string, IReadOnlyList<LlmLink>> Chains { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [AiChains.Assistant] = [new(GeminiProviderName, FlashLatest), new(GeminiProviderName, FlashLiteLatest)],
        [AiChains.Categorize] = [new(GeminiProviderName, FlashLatest), new(GeminiProviderName, FlashLiteLatest)],
        [AiChains.Daily] = [new(GeminiProviderName, FlashLatest), new(GeminiProviderName, FlashLiteLatest)],
        [AiChains.Weekly] = [new(GeminiProviderName, Flash3Preview), new(GeminiProviderName, FlashLatest), new(GeminiProviderName, FlashLiteLatest)],
        [AiChains.Education] = [new(GeminiProviderName, Flash3Preview), new(GeminiProviderName, FlashLatest), new(GeminiProviderName, FlashLiteLatest)],
    };

    /// <summary>Configured limits. A model without an entry gets the default of <see cref="LimitFor"/>.</summary>
    public List<AiLimit> Limits { get; } = new();

    /// <summary>
    /// The limits of a model. Gemini models without an entry: only 5 requests per minute (the lowest number quoted by
    /// third parties, to pace the jobs); their daily quotas are not published and were not measured, so they stay
    /// unknown and the chain learns them from the 429.
    /// </summary>
    public AiLimit LimitFor(string provider, string model)
    {
        var configured = Limits.LastOrDefault(l =>
            string.Equals(l.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Model, model, StringComparison.OrdinalIgnoreCase));
        if (configured is not null) return configured;

        return string.Equals(provider, GeminiProviderName, StringComparison.OrdinalIgnoreCase)
            ? new AiLimit { Provider = provider, Model = model, Rpm = 5 }
            : new AiLimit { Provider = provider, Model = model };
    }

    /// <summary>A known limit is used up to 90%; never less than one.</summary>
    public static long Usable(long limit) => Math.Max(1, limit * 9 / 10);
}
