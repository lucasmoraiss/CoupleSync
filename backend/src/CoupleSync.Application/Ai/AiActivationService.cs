using System.Globalization;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Ai;

/// <summary>
/// "Can the AI be used at all?" (design 7.4): not switched off by <c>Ai__Disabled</c> and at least one provider
/// with a key — or the fake provider on. It says nothing about the group: that is the consent.
/// </summary>
public sealed class AiAvailability
{
    private readonly ILlmProviderCatalog _catalog;
    private readonly AiOptions _options;

    public AiAvailability(ILlmProviderCatalog catalog, IOptions<AiOptions> options)
    {
        _catalog = catalog;
        _options = options.Value;
    }

    public bool IsAvailable => !_options.Disabled && _catalog.AnyAvailable;
}

/// <summary>The server's answer to "has this group switched the AI on?", read from ai_consents on every call.</summary>
public sealed class ServerAiConsentGate : IAiConsentGate
{
    private readonly IAiActivationRepository _repository;

    public ServerAiConsentGate(IAiActivationRepository repository)
    {
        _repository = repository;
    }

    /// <summary>A call without a group has nobody who could have accepted: nothing is sent.</summary>
    public Task<bool> IsEnabledAsync(Guid? coupleId, CancellationToken ct)
        => coupleId is { } couple ? _repository.IsEnabledAsync(couple, AiConsent.CurrentVersion, ct) : Task.FromResult(false);
}

/// <param name="Name">How the text of privacy names the destination.</param>
/// <param name="TrainsOnData">Whether the provider may use what is sent to improve its products.</param>
public sealed record AiProviderInfo(string Name, string Country, bool TrainsOnData);

public sealed record AiFeatureFlags(bool Assistant, bool Insights, bool Education, bool WeeklyEmail);

/// <param name="ResetsAtLocal">The next midnight of Brasília, as local time (yyyy-MM-ddTHH:mm:ss).</param>
public sealed record AiBudgetStatus(int CallsToday, int CallLimit, string ResetsAtLocal);

public sealed record AiMyAcceptance(DateTime AcceptedAtUtc);

public sealed record AiStatus(
    bool Available,
    bool Enabled,
    int ConsentVersion,
    IReadOnlyList<AiAcceptance> AcceptedBy,
    AiMyAcceptance? MyAcceptance,
    bool OnboardingPending,
    bool WeeklyEmailEnabled,
    bool EmailVerified,
    bool EmailConfigured,
    IReadOnlyList<AiProviderInfo> Providers,
    AiFeatureFlags Features,
    AiBudgetStatus Budget);

public sealed record AiUsageDay(DateOnly Day, int Calls, long InputTokens, long OutputTokens, int Failures);

public sealed record AiUsageFeature(string Feature, int Calls, long InputTokens, long OutputTokens);

/// <param name="Limit">The daily quota of the model, when it is known (configured). Null: not known yet.</param>
/// <param name="PercentUsed">How much of that quota this group used today. Null while the quota is not known.</param>
public sealed record AiUsageProvider(string Name, string Model, int Calls, int? Limit, int? PercentUsed, bool ExhaustedToday);

public sealed record AiGroupBudget(int CallsToday, int CallLimit, long TokensToday, long TokenLimit, string ResetsAtLocal);

public sealed record AiUsageReport(
    IReadOnlyList<AiUsageDay> Days,
    IReadOnlyList<AiUsageFeature> ByFeature,
    IReadOnlyList<AiUsageProvider> ProvidersToday,
    AiGroupBudget GroupBudget);

/// <summary>
/// Switching the AI on and off for a group, and what the app shows about it (design 7 and 10.2). One acceptance is
/// enough and counts for the whole group; any member switches it off. Everything here is about the group of the
/// token — the controller passes it — and ai_usage is always read with that group named.
/// </summary>
public sealed class AiActivationService
{
    public const string ScopeMine = "mine";
    public const string ScopeGroup = "group";
    public const int MinUsageDays = 1;
    public const int MaxUsageDays = 90;

    /// <summary>Google is always named, as it has been since version 1 of the text (design 7.3).</summary>
    private static readonly AiProviderInfo Google = new("Google (Gemini)", "Estados Unidos", TrainsOnData: true);

    /// <summary>
    /// The other providers the consent text covers, as the person is told about them. Groq LLC is in the United
    /// States and its terms forbid it to train on what is sent (Groq Services Agreement, 4.2, read on 2026-10-08).
    /// A provider is only named while it really receives data: see <see cref="ProvidersInUse"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, AiProviderInfo> OtherProviders =
        new Dictionary<string, AiProviderInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [AiOptions.GroqProviderName] = new("Groq", "Estados Unidos", TrainsOnData: false),
        };

    private readonly IAiActivationRepository _repository;
    private readonly IAiUsageRepository _usage;
    private readonly AiAvailability _availability;
    private readonly ILlmProviderCatalog _catalog;
    private readonly IEmailSender _emailSender;
    private readonly IDateTimeProvider _clock;
    private readonly AiOptions _options;

    public AiActivationService(
        IAiActivationRepository repository,
        IAiUsageRepository usage,
        AiAvailability availability,
        ILlmProviderCatalog catalog,
        IEmailSender emailSender,
        IDateTimeProvider clock,
        IOptions<AiOptions> options)
    {
        _repository = repository;
        _usage = usage;
        _availability = availability;
        _catalog = catalog;
        _emailSender = emailSender;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AiStatus> GetStatusAsync(Guid coupleId, Guid userId, CancellationToken ct)
    {
        var available = _availability.IsAvailable;
        var acceptedBy = await _repository.GetAcceptancesAsync(coupleId, AiConsent.CurrentVersion, ct);
        var mine = acceptedBy.FirstOrDefault(a => a.UserId == userId);
        var preference = await _repository.FindPreferenceAsync(coupleId, userId, ct);
        var today = Today();
        var groupDay = await _usage.GetGroupDayAsync(coupleId, today, LlmFeatures.Interactive, ct);

        return new AiStatus(
            available,
            Enabled: acceptedBy.Count > 0,
            AiConsent.CurrentVersion,
            acceptedBy,
            mine is null ? null : new AiMyAcceptance(mine.AcceptedAtUtc),
            OnboardingPending: available && IsOnboardingPending(preference, mine, acceptedBy, await HasOldAcceptanceAsync(coupleId, userId, ct)),
            WeeklyEmailEnabled: preference?.WeeklyEmailEnabled ?? false,
            EmailVerified: await _repository.IsEmailVerifiedAsync(userId, ct),
            EmailConfigured: _emailSender.IsConfigured,
            ProvidersInUse(),
            // Only the Assistant exists in this phase; the others arrive with their own phases.
            new AiFeatureFlags(Assistant: available, Insights: false, Education: false, WeeklyEmail: false),
            new AiBudgetStatus(groupDay.Calls, _options.GroupDailyCalls, ResetsAtLocal(today)));
    }

    /// <summary>
    /// Where the data goes NOW: Google, and each other covered provider that a chain can really reach — it has a
    /// key and a link in some chain. Without the key of Groq the list is the one of version 1, only Google.
    /// </summary>
    private IReadOnlyList<AiProviderInfo> ProvidersInUse()
    {
        var providers = new List<AiProviderInfo> { Google };
        if (_options.UseFakeProvider) return providers;

        var links = AiChains.All.SelectMany(chain => _options.Chains.GetValueOrDefault(chain) ?? []).ToList();
        foreach (var name in AiConsentCoverage.Providers)
        {
            if (!OtherProviders.TryGetValue(name, out var info)) continue;
            var reachable = links.Any(link =>
                string.Equals(link.Provider, name, StringComparison.OrdinalIgnoreCase)
                && _catalog.Find(link.Provider, link.Model) is not null);
            if (reachable) providers.Add(info);
        }

        return providers;
    }

    /// <summary>
    /// The welcome screen is due when the person never answered it, when someone else switched the AI on after
    /// their last answer — the other member is told inside the app and can switch it off (design 7.4, decision 10) —
    /// or when the person had accepted an earlier version of the text: the text changed, so the question comes
    /// back to who had said yes (design 7.1). An acceptance of an earlier version never counts as "switched on".
    /// </summary>
    private static bool IsOnboardingPending(AiUserPreference? preference, AiAcceptance? mine, IReadOnlyList<AiAcceptance> acceptedBy, bool hasOldAcceptance)
    {
        if (mine is not null) return false;
        if (hasOldAcceptance) return true;
        if (preference?.OnboardingAnsweredAtUtc is not { } answeredAt) return true;
        return acceptedBy.Any(a => a.AcceptedAtUtc > answeredAt);
    }

    /// <summary>The person's acceptances of this group that are not revoked and are of a version that is no longer in force.</summary>
    private async Task<IReadOnlyList<AiConsent>> OldAcceptancesAsync(Guid coupleId, Guid userId, CancellationToken ct)
        => (await _repository.GetActiveConsentsAsync(coupleId, ct))
            .Where(c => c.UserId == userId && c.Version != AiConsent.CurrentVersion)
            .ToList();

    private async Task<bool> HasOldAcceptanceAsync(Guid coupleId, Guid userId, CancellationToken ct)
        => (await OldAcceptancesAsync(coupleId, userId, ct)).Count > 0;

    public async Task<AiStatus> AcceptAsync(Guid coupleId, Guid userId, int version, CancellationToken ct)
    {
        if (!_availability.IsAvailable)
            throw new AppException("AI_UNAVAILABLE", "A análise com IA não está disponível no momento.", 503);

        if (version != AiConsent.CurrentVersion)
        {
            throw new ConflictException(
                "AI_CONSENT_VERSION_OUTDATED",
                "O texto sobre a análise com IA mudou. Atualize o app e leia o texto novo antes de ativar.");
        }

        var now = _clock.UtcNow;
        var consent = await _repository.FindConsentAsync(coupleId, userId, version, ct);
        if (consent is null) await _repository.AddConsentAsync(AiConsent.Accept(coupleId, userId, version, now), ct);
        else consent.AcceptAgain(now);
        await SaveIgnoringTheSameAnswerTwiceAsync(ct);

        // Who accepts has answered the welcome question: it does not come back on another device.
        await AnswerOnboardingAsync(coupleId, userId, now, ct);

        return await GetStatusAsync(coupleId, userId, ct);
    }

    /// <param name="scope">"mine": only the person's own acceptance. "group": every acceptance of the group.</param>
    public async Task<AiStatus> RevokeAsync(Guid coupleId, Guid userId, string? scope, CancellationToken ct)
    {
        if (scope is not (ScopeMine or ScopeGroup))
            throw new BadRequestException("INVALID_SCOPE", "Informe scope=mine ou scope=group.");

        var now = _clock.UtcNow;
        foreach (var consent in await _repository.GetActiveConsentsAsync(coupleId, ct))
        {
            if (scope == ScopeGroup || consent.UserId == userId) consent.Revoke(now, userId);
        }

        await _repository.SaveChangesAsync(ct);

        // Switching it off is also an answer to the welcome question.
        await AnswerOnboardingAsync(coupleId, userId, now, ct);

        return await GetStatusAsync(coupleId, userId, ct);
    }

    public async Task<AiStatus> UpdatePreferencesAsync(Guid coupleId, Guid userId, bool? weeklyEmail, bool? onboardingAnswered, CancellationToken ct)
    {
        if (weeklyEmail == true)
        {
            if (!_emailSender.IsConfigured)
                throw new AppException("EMAIL_NOT_CONFIGURED", "O envio de e-mails não está disponível no momento.", 503);
            if (!await _repository.IsEmailVerifiedAsync(userId, ct))
                throw new UnprocessableEntityException("EMAIL_NOT_VERIFIED", "Confirme o seu e-mail antes de ativar o resumo semanal.");
        }

        if (weeklyEmail is not null || onboardingAnswered == true)
        {
            var now = _clock.UtcNow;
            var preference = await PreferenceOfAsync(coupleId, userId, now, ct);
            if (weeklyEmail is { } enabled) preference.SetWeeklyEmail(enabled, now);
            if (onboardingAnswered == true)
            {
                preference.AnswerOnboarding(now);
                // "Not now" to a new version of the text: the acceptance of the earlier one is over, by the person's
                // own answer, and the question does not come back.
                foreach (var old in await OldAcceptancesAsync(coupleId, userId, ct)) old.Revoke(now, userId);
            }

            await SaveIgnoringTheSameAnswerTwiceAsync(ct);
        }

        return await GetStatusAsync(coupleId, userId, ct);
    }

    public async Task<AiUsageReport> GetUsageAsync(Guid coupleId, int days, CancellationToken ct)
    {
        if (days is < MinUsageDays or > MaxUsageDays)
            throw new BadRequestException("INVALID_DAYS", "Informe um número de dias entre 1 e 90.");

        var today = Today();
        var from = today.AddDays(1 - days);
        var rows = await _usage.GetGroupSummaryAsync(coupleId, from, today, ct);

        var byDay = rows.GroupBy(r => r.DayBrt).ToDictionary(g => g.Key, g => g.ToList());
        var dayList = new List<AiUsageDay>(days);
        for (var day = from; day <= today; day = day.AddDays(1))
        {
            var ofDay = byDay.GetValueOrDefault(day) ?? [];
            dayList.Add(new AiUsageDay(
                day,
                ofDay.Sum(r => r.Calls),
                ofDay.Sum(r => r.InputTokens),
                ofDay.Sum(r => r.OutputTokens),
                ofDay.Where(r => r.Outcome != nameof(LlmOutcome.Ok)).Sum(r => r.Calls)));
        }

        var byFeature = rows
            .GroupBy(r => r.Feature, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AiUsageFeature(g.Key, g.Sum(r => r.Calls), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens)))
            .ToList();

        var groupDay = await _usage.GetGroupDayAsync(coupleId, today, LlmFeatures.Interactive, ct);

        return new AiUsageReport(
            dayList,
            byFeature,
            await ProvidersTodayAsync(coupleId, ct),
            new AiGroupBudget(groupDay.Calls, _options.GroupDailyCalls, groupDay.Tokens, _options.GroupDailyTokens, ResetsAtLocal(today)));
    }

    /// <summary>
    /// The models that can answer now, with what THIS group sent to each one in the provider's day (UTC). The quota
    /// of a model is only shown when it is known (configured); whether it is exhausted is a state of the model, read
    /// the same way the chain reads it.
    /// </summary>
    private async Task<IReadOnlyList<AiUsageProvider>> ProvidersTodayAsync(Guid coupleId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var sent = await _usage.GetGroupModelDayAsync(coupleId, DateOnly.FromDateTime(now), ct);

        IEnumerable<LlmLink> links = _options.UseFakeProvider
            ? [new LlmLink(AiOptions.FakeProviderName, AiOptions.FakeProviderName)]
            : AiChains.All.SelectMany(chain => _options.Chains.GetValueOrDefault(chain) ?? []);

        var result = new List<AiUsageProvider>();
        foreach (var link in links.Distinct())
        {
            if (_catalog.Find(link.Provider, link.Model) is null) continue;

            var calls = sent
                .Where(s => string.Equals(s.Provider, link.Provider, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(s.Model, link.Model, StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.Calls);
            var limit = _options.LimitFor(link.Provider, link.Model).Rpd;
            var events = await _usage.GetRecentLinkEventsAsync(link.Provider, link.Model, now - LlmLinkState.Lookback, LlmLinkState.MaxEvents, ct);

            result.Add(new AiUsageProvider(
                link.Provider,
                link.Model,
                calls,
                limit,
                limit is > 0 ? (int)Math.Min(100, (long)calls * 100 / limit.Value) : null,
                LlmLinkState.From(events, now).ExhaustedToday));
        }

        return result;
    }

    private async Task<AiUserPreference> PreferenceOfAsync(Guid coupleId, Guid userId, DateTime now, CancellationToken ct)
    {
        var preference = await _repository.FindPreferenceAsync(coupleId, userId, ct);
        if (preference is not null) return preference;

        preference = AiUserPreference.Create(coupleId, userId, now);
        await _repository.AddPreferenceAsync(preference, ct);
        return preference;
    }

    private async Task AnswerOnboardingAsync(Guid coupleId, Guid userId, DateTime now, CancellationToken ct)
    {
        (await PreferenceOfAsync(coupleId, userId, now, ct)).AnswerOnboarding(now);
        await SaveIgnoringTheSameAnswerTwiceAsync(ct);
    }

    /// <summary>
    /// Each write is saved on its own, so that losing a race on one row never drops another change (the repository
    /// forgets what it could not store).
    /// </summary>
    private async Task SaveIgnoringTheSameAnswerTwiceAsync(CancellationToken ct)
    {
        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (UniqueViolationException)
        {
            // Two devices of the same person answered at the same instant: the other request stored the same row.
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(BrazilTime.ToLocal(_clock.UtcNow));

    private static string ResetsAtLocal(DateOnly today)
        => today.AddDays(1).ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
