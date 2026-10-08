using System.Globalization;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Integrations.Gemini;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Llm;

/// <summary>An OpenAI-compatible provider as configured: Ai__OpenAiCompatible__0__Name=groq, __BaseUrl, __ApiKeyVariable=GROQ_API_KEY.</summary>
public sealed class OpenAiCompatibleEntry
{
    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The NAME of the environment variable that holds the key (the key itself is never in the "Ai" section).</summary>
    public string ApiKeyVariable { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Keys and addresses of the providers. Infrastructure only: the Application layer never sees a key.</summary>
public sealed class LlmProvidersOptions
{
    public string GeminiEndpoint { get; set; } = string.Empty;

    public string GeminiApiKey { get; set; } = string.Empty;

    public int GeminiThinkingHeadroomTokens { get; set; } = GeminiOptions.DefaultThinkingHeadroomTokens;

    public List<OpenAiCompatibleEntry> OpenAiCompatible { get; } = new();
}

/// <summary>
/// Reads the "Ai" section by hand, with the invariant culture: indexed lists that REPLACE the defaults (the binder
/// would append to them) and limits in which an empty value means "unknown", not zero.
/// </summary>
public static class AiConfiguration
{
    public const string GeminiKeyVariable = "GEMINI_API_KEY";

    public static void Apply(AiOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);

        options.Disabled = Flag(section[nameof(AiOptions.Disabled)]) ?? options.Disabled;
        options.UseFakeProvider = Flag(section[nameof(AiOptions.UseFakeProvider)]) ?? options.UseFakeProvider;
        options.GroupDailyCalls = (int?)Number(section[nameof(AiOptions.GroupDailyCalls)]) ?? options.GroupDailyCalls;
        options.GroupDailyTokens = Number(section[nameof(AiOptions.GroupDailyTokens)]) ?? options.GroupDailyTokens;
        options.JobDailyCalls = (int?)Number(section[nameof(AiOptions.JobDailyCalls)]) ?? options.JobDailyCalls;
        options.GlobalDailyInteractiveCalls = (int?)Number(section[nameof(AiOptions.GlobalDailyInteractiveCalls)]) ?? options.GlobalDailyInteractiveCalls;

        foreach (var chain in section.GetSection(nameof(AiOptions.Chains)).GetChildren())
        {
            var links = chain.GetChildren()
                .OrderBy(link => Number(link.Key) ?? long.MaxValue)
                .Select(link => ParseLink(link.Value))
                .Where(link => link is not null)
                .Select(link => link!)
                .ToList();
            if (links.Count > 0) options.Chains[chain.Key] = links;
        }

        foreach (var entry in section.GetSection(nameof(AiOptions.Limits)).GetChildren())
        {
            var provider = entry[nameof(AiLimit.Provider)]?.Trim();
            var model = entry[nameof(AiLimit.Model)]?.Trim();
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) continue;

            options.Limits.Add(new AiLimit
            {
                Provider = provider,
                Model = model,
                Rpd = (int?)Number(entry[nameof(AiLimit.Rpd)]),
                Tpd = Number(entry[nameof(AiLimit.Tpd)]),
                Rpm = (int?)Number(entry[nameof(AiLimit.Rpm)]),
                Tpm = Number(entry[nameof(AiLimit.Tpm)]),
            });
        }
    }

    public static void Apply(LlmProvidersOptions options, IConfiguration configuration, GeminiOptions gemini)
    {
        options.GeminiEndpoint = gemini.Endpoint;
        options.GeminiThinkingHeadroomTokens = gemini.ThinkingHeadroomTokens;
        options.GeminiApiKey = EffectiveGeminiKey(configuration, gemini.ApiKey);

        foreach (var entry in CompatibleProviders(configuration))
        {
            entry.ApiKey = string.IsNullOrWhiteSpace(entry.ApiKeyVariable) ? string.Empty : (configuration[entry.ApiKeyVariable] ?? string.Empty).Trim();
            options.OpenAiCompatible.Add(entry);
        }
    }

    /// <summary>The Gemini key the API would use: the options (which carry every override), else the variable.</summary>
    public static string EffectiveGeminiKey(IConfiguration configuration, string? fromOptions)
    {
        foreach (var candidate in new[] { fromOptions, configuration[GeminiKeyVariable], configuration["Gemini:ApiKey"] })
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        return string.Empty;
    }

    /// <summary>The configured OpenAI-compatible providers (those with a name), without their keys.</summary>
    public static List<OpenAiCompatibleEntry> CompatibleProviders(IConfiguration configuration)
        => configuration.GetSection($"{AiOptions.SectionName}:OpenAiCompatible").GetChildren()
            .Select(entry => new OpenAiCompatibleEntry
            {
                Name = (entry[nameof(OpenAiCompatibleEntry.Name)] ?? string.Empty).Trim(),
                BaseUrl = (entry[nameof(OpenAiCompatibleEntry.BaseUrl)] ?? string.Empty).Trim(),
                ApiKeyVariable = (entry[nameof(OpenAiCompatibleEntry.ApiKeyVariable)] ?? string.Empty).Trim(),
            })
            .Where(entry => entry.Name.Length > 0)
            .ToList();

    private static LlmLink? ParseLink(string? value)
    {
        var parts = (value ?? string.Empty).Split('|', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? new LlmLink(parts[0], parts[1]) : null;
    }

    private static bool? Flag(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static long? Number(string? value)
        => long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// The API refuses to start with the fake provider on when (a) a real provider key has a value — GEMINI_API_KEY,
/// which has one in production, or the key of any configured compatible provider — or (b) the RENDER variable
/// exists (Render sets RENDER=true on every service, in every runtime). It does not look at ASPNETCORE_ENVIRONMENT:
/// "App E2E" runs the image as Production. On Render a mistaken Ai__UseFakeProvider makes the new version fail at
/// start-up, the deploy is marked as failed and the previous version stays up: the fake never answers real users.
/// </summary>
public static class FakeLlmProviderGuard
{
    public const string RenderVariable = "RENDER";

    public static void EnsureSafe(IConfiguration configuration, string? effectiveGeminiKey = null)
    {
        var ai = new AiOptions();
        AiConfiguration.Apply(ai, configuration);
        if (!ai.UseFakeProvider) return;

        string? reason = null;
        if (AiConfiguration.EffectiveGeminiKey(configuration, effectiveGeminiKey).Length > 0)
            reason = $"{AiConfiguration.GeminiKeyVariable} has a value";
        else if (AiConfiguration.CompatibleProviders(configuration).FirstOrDefault(HasKey) is { } provider)
            reason = $"{provider.ApiKeyVariable} (provider '{provider.Name}') has a value";
        else if (!string.IsNullOrEmpty(configuration[RenderVariable]))
            reason = $"the {RenderVariable} variable exists (this is a Render service)";

        if (reason is not null)
        {
            throw new InvalidOperationException(
                $"Ai__UseFakeProvider=true is only for tests: the API does not start with the fake AI provider because {reason}. " +
                "Remove Ai__UseFakeProvider from this environment.");
        }

        bool HasKey(OpenAiCompatibleEntry entry)
            => entry.ApiKeyVariable.Length > 0 && !string.IsNullOrWhiteSpace(configuration[entry.ApiKeyVariable]);
    }
}

/// <summary>Who can be called now. A provider without a key does not exist for the chains.</summary>
public sealed class LlmProviderCatalog : ILlmProviderCatalog
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiOptions _ai;
    private readonly LlmProvidersOptions _providers;

    public LlmProviderCatalog(IHttpClientFactory httpClientFactory, IOptions<AiOptions> ai, IOptions<LlmProvidersOptions> providers)
    {
        _httpClientFactory = httpClientFactory;
        _ai = ai.Value;
        _providers = providers.Value;
    }

    public bool AnyAvailable
        => _ai.UseFakeProvider || _providers.GeminiApiKey.Length > 0 || _providers.OpenAiCompatible.Any(IsUsable);

    public ILlmProvider? Find(string provider, string model)
    {
        if (string.Equals(provider, AiOptions.FakeProviderName, StringComparison.OrdinalIgnoreCase))
            return _ai.UseFakeProvider ? new FakeLlmProvider() : null;

        if (string.Equals(provider, AiOptions.GeminiProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return _providers.GeminiApiKey.Length > 0
                ? new GeminiLlmProvider(_httpClientFactory, _providers.GeminiEndpoint, _providers.GeminiApiKey, model, _providers.GeminiThinkingHeadroomTokens)
                : null;
        }

        var compatible = _providers.OpenAiCompatible.FirstOrDefault(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));
        return compatible is not null && IsUsable(compatible)
            ? new OpenAiCompatibleLlmProvider(_httpClientFactory, compatible.Name, compatible.BaseUrl, compatible.ApiKey, model)
            : null;
    }

    private static bool IsUsable(OpenAiCompatibleEntry entry) => entry.ApiKey.Length > 0 && entry.BaseUrl.Length > 0;
}
