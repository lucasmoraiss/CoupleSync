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

    /// <summary>The second provider (OpenAI-compatible). It only exists for the chains while GROQ_API_KEY has a value.</summary>
    public const string GroqProviderName = "groq";

    // PROVISIONAL (2026-10-08): chosen from Groq's public documentation, not yet checked with the real key. A
    // production model that accepts a strict JSON schema; published free limits per model: 30 requests and 8,000
    // tokens per minute, 1,000 requests and 200,000 tokens per day. This is the one place to change the model.
    public const string GroqReserveModel = "openai/gpt-oss-120b";

    public const int GroqRpm = 30;
    public const long GroqTpm = 8_000;
    public const int GroqRpd = 1_000;
    public const long GroqTpd = 200_000;

    // Measured with the real key on 2026-10-08: the alias gemini-flash-latest is the most capable Flash that answers
    // (it resolves to 3.8); gemini-flash-lite-latest resolves to 3.5 lite; the three fixed ids answer as themselves.
    // Five different models, each with a quota of its own.
    private const string FlashLatest = "gemini-flash-latest";
    private const string FlashLiteLatest = "gemini-flash-lite-latest";
    private const string Flash3Preview = "gemini-3-flash-preview";
    private const string Flash25 = "gemini-2.5-flash";
    private const string Flash31Lite = "gemini-3.1-flash-lite";

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

    /// <summary>
    /// Quality first where there are few calls and the text matters (weekly, monthly, guide): the most capable Flash,
    /// then the preview, then the lite alias. Speed and quota first where there is volume (Assistant, daily insight,
    /// categorization): lite alias, then two fixed models — never the two models of the summaries, so that a day
    /// full of conversation cannot exhaust them (design 2.2).
    /// Groq closes every chain as the reserve, after the Gemini links: who uses the app today gets the same answer
    /// from the same model, and Groq only answers when no Gemini link could (quota over, model withdrawn, Google
    /// down). Without GROQ_API_KEY its link resolves to no provider and the chain is the Gemini one.
    /// </summary>
    public Dictionary<string, IReadOnlyList<LlmLink>> Chains { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [AiChains.Assistant] = [new(GeminiProviderName, FlashLiteLatest), new(GeminiProviderName, Flash25), new(GeminiProviderName, Flash31Lite), GroqReserve],
        [AiChains.Categorize] = [new(GeminiProviderName, FlashLiteLatest), new(GeminiProviderName, Flash25), new(GeminiProviderName, Flash31Lite), GroqReserve],
        [AiChains.Daily] = [new(GeminiProviderName, FlashLiteLatest), new(GeminiProviderName, Flash25), new(GeminiProviderName, Flash31Lite), GroqReserve],
        [AiChains.Weekly] = [new(GeminiProviderName, FlashLatest), new(GeminiProviderName, Flash3Preview), new(GeminiProviderName, FlashLiteLatest), GroqReserve],
        [AiChains.Education] = [new(GeminiProviderName, FlashLatest), new(GeminiProviderName, Flash3Preview), new(GeminiProviderName, FlashLiteLatest), GroqReserve],
    };

    private static LlmLink GroqReserve => new(GroqProviderName, GroqReserveModel);

    /// <summary>Configured limits. A model without an entry gets the default of <see cref="LimitFor"/>.</summary>
    public List<AiLimit> Limits { get; } = new();

    /// <summary>
    /// The limits of a model. Gemini models without an entry get only a pace per minute: 5 requests (the lowest
    /// number quoted by third parties, to pace the jobs), or 10 for the "lite" models (seven calls in a row to
    /// gemini-flash-lite-latest went through with the real key). Their daily quotas are not published and were not
    /// measured, so they stay unknown and the chain learns them from the 429. Groq models get the limits Groq
    /// publishes for the free plan, which are per model. Replace per model with Ai__Limits.
    /// </summary>
    public AiLimit LimitFor(string provider, string model)
    {
        var configured = Limits.LastOrDefault(l =>
            string.Equals(l.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Model, model, StringComparison.OrdinalIgnoreCase));
        if (configured is not null) return configured;

        if (string.Equals(provider, GroqProviderName, StringComparison.OrdinalIgnoreCase))
            return new AiLimit { Provider = provider, Model = model, Rpd = GroqRpd, Tpd = GroqTpd, Rpm = GroqRpm, Tpm = GroqTpm };

        if (!string.Equals(provider, GeminiProviderName, StringComparison.OrdinalIgnoreCase))
            return new AiLimit { Provider = provider, Model = model };

        var lite = model.Contains("lite", StringComparison.OrdinalIgnoreCase);
        return new AiLimit { Provider = provider, Model = model, Rpm = lite ? DefaultGeminiLiteRpm : DefaultGeminiRpm };
    }

    public const int DefaultGeminiRpm = 5;
    public const int DefaultGeminiLiteRpm = 10;

    /// <summary>A known limit is used up to 90%; never less than one.</summary>
    public static long Usable(long limit) => Math.Max(1, limit * 9 / 10);
}
