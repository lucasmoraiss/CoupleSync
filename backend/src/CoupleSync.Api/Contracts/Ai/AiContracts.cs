namespace CoupleSync.Api.Contracts.Ai;

public sealed record AiConsentRequest(int Version);

/// <summary>Both fields are optional: only what is sent changes.</summary>
public sealed record AiPreferencesRequest(bool? WeeklyEmail, bool? OnboardingAnswered);

public sealed record AiAcceptedByResponse(Guid UserId, string Name, DateTime AcceptedAtUtc);

public sealed record AiMyAcceptanceResponse(DateTime AcceptedAtUtc);

public sealed record AiProviderResponse(string Name, string Country, bool TrainsOnData);

public sealed record AiFeaturesResponse(bool Assistant, bool Insights, bool Education, bool WeeklyEmail);

/// <param name="ResetsAtLocal">The next midnight of Brasília, in local time (yyyy-MM-ddTHH:mm:ss).</param>
public sealed record AiBudgetResponse(int CallsToday, int CallLimit, string ResetsAtLocal);

/// <param name="Available">The AI can be used at all (not switched off on the server and a provider has a key).</param>
/// <param name="Enabled">The group switched it on: at least one acceptance in force of a current member.</param>
/// <param name="MyAcceptance">Null when the person has no acceptance in force.</param>
/// <param name="OnboardingPending">The welcome screen is due for this person in this group.</param>
public sealed record AiStatusResponse(
    bool Available,
    bool Enabled,
    int ConsentVersion,
    IReadOnlyList<AiAcceptedByResponse> AcceptedBy,
    AiMyAcceptanceResponse? MyAcceptance,
    bool OnboardingPending,
    bool WeeklyEmailEnabled,
    bool EmailVerified,
    bool EmailConfigured,
    IReadOnlyList<AiProviderResponse> Providers,
    AiFeaturesResponse Features,
    AiBudgetResponse Budget);

/// <param name="Day">A Brasília day, yyyy-MM-dd.</param>
public sealed record AiUsageDayResponse(string Day, int Calls, long InputTokens, long OutputTokens, int Failures);

public sealed record AiUsageFeatureResponse(string Feature, int Calls, long InputTokens, long OutputTokens);

/// <param name="Limit">The daily quota of the model; null while it is not known.</param>
/// <param name="PercentUsed">How much of that quota this group used today; null while the quota is not known.</param>
public sealed record AiUsageProviderResponse(string Name, string Model, int Calls, int? Limit, int? PercentUsed, bool ExhaustedToday);

public sealed record AiGroupBudgetResponse(int CallsToday, int CallLimit, long TokensToday, long TokenLimit, string ResetsAtLocal);

public sealed record AiUsageResponse(
    IReadOnlyList<AiUsageDayResponse> Days,
    IReadOnlyList<AiUsageFeatureResponse> ByFeature,
    IReadOnlyList<AiUsageProviderResponse> ProvidersToday,
    AiGroupBudgetResponse GroupBudget);
