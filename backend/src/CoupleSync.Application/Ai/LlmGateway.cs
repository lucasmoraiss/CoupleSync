using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Ai;

/// <summary>
/// The chain every AI call goes through (design 2.4 and 2.5): consent, emergency switch, budgets, then the links of
/// the feature's chain in order, each one skipped when it has no key, is not covered by the consent, is paused or
/// exhausted, or would go over a known limit. Every call that reaches a provider leaves a row in ai_usage, and
/// everything that must survive a restart (budgets, pauses, exhausted links) is read back from those rows.
/// Logs carry provider, model, feature, outcome and time — never the prompt, the answer or a secret.
/// </summary>
public sealed class LlmGateway : ILlmGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmProviderCatalog _catalog;
    private readonly IAiUsageRepository _usage;
    private readonly IAiConsentGate _consent;
    private readonly AiOptions _options;
    private readonly LlmMinuteWindow _window;
    private readonly IDateTimeProvider _clock;
    private readonly ILlmWaiter _waiter;
    private readonly ILogger<LlmGateway> _logger;

    public LlmGateway(
        ILlmProviderCatalog catalog,
        IAiUsageRepository usage,
        IAiConsentGate consent,
        IOptions<AiOptions> options,
        LlmMinuteWindow window,
        IDateTimeProvider clock,
        ILlmWaiter waiter,
        ILogger<LlmGateway> logger)
    {
        _catalog = catalog;
        _usage = usage;
        _consent = consent;
        _options = options.Value;
        _window = window;
        _clock = clock;
        _waiter = waiter;
        _logger = logger;
    }

    public Task<LlmGatewayResult<T>> GenerateAsync<T>(Guid? coupleId, LlmRequest request, LlmCallMode mode, CancellationToken ct)
        where T : class
        => GenerateAsync<T>(coupleId, request, mode, accept: null, ct);

    public async Task<LlmGatewayResult<T>> GenerateAsync<T>(
        Guid? coupleId,
        LlmRequest request,
        LlmCallMode mode,
        Func<T, bool>? accept,
        CancellationToken ct)
        where T : class
    {
        var chain = LlmFeatures.ChainOf(request.Feature);

        // Rules 1 to 3: nothing is sent.
        if (!await _consent.IsEnabledAsync(coupleId, ct)) return Stop<T>(LlmGatewayOutcome.NotConsented, request);
        if (_options.Disabled) return Stop<T>(LlmGatewayOutcome.Disabled, request);
        var overBudget = await BudgetOutcomeAsync(coupleId, request.Feature, ct);
        if (overBudget is { } budgetOutcome) return Stop<T>(budgetOutcome, request);

        IReadOnlyList<LlmLink> links;
        if (_options.UseFakeProvider) links = [new LlmLink(AiOptions.FakeProviderName, AiOptions.FakeProviderName)];
        else if (_options.Chains.TryGetValue(chain, out var configured)) links = configured;
        else links = [];

        var startedAt = _clock.UtcNow;
        var estimatedTokens = PromptText.EstimateTokens(request);
        var rejections = 0;

        // Rule 4: link by link.
        foreach (var link in links)
        {
            var isFake = string.Equals(link.Provider, AiOptions.FakeProviderName, StringComparison.OrdinalIgnoreCase);
            if (!isFake && !AiConsentCoverage.Covers(link.Provider))
            {
                _logger.LogWarning(
                    "AI link {Provider}|{Model} of the chain {Chain} ignored: the provider is not covered by the current AI consent.",
                    link.Provider, link.Model, chain);
                continue;
            }

            var provider = _catalog.Find(link.Provider, link.Model);
            if (provider is null) continue;

            var now = _clock.UtcNow;
            var events = await _usage.GetRecentLinkEventsAsync(link.Provider, link.Model, now - LlmLinkState.Lookback, LlmLinkState.MaxEvents, ct);
            var state = LlmLinkState.From(events, now);
            if (!state.IsAvailable(now)) continue;

            var limit = _options.LimitFor(link.Provider, link.Model);
            if (await WouldExceedTheDayAsync(link, limit, estimatedTokens, now, ct)) continue;
            if (!await ReserveMinuteAsync(link, limit, estimatedTokens, mode, ct)) continue;

            TimeSpan timeout;
            if (mode == LlmCallMode.Interactive)
            {
                var remaining = _options.InteractiveBudget - (_clock.UtcNow - startedAt);
                if (remaining <= TimeSpan.Zero) break;
                timeout = remaining < _options.LinkTimeout ? remaining : _options.LinkTimeout;
            }
            else
            {
                timeout = _options.JobLinkTimeout;
            }

            // Rule 1 again, right before the call: a group that switched the AI off stops a job at its next call.
            if (!await _consent.IsEnabledAsync(coupleId, ct)) return Stop<T>(LlmGatewayOutcome.NotConsented, request);

            var result = await CallAsync(provider, request, timeout, ct);
            var outcome = result.Outcome;
            T? value = null;
            if (outcome == LlmOutcome.Ok && !TryRead(result.Json, request.ResponseSchema, out value))
                outcome = LlmOutcome.InvalidOutput;
            if (outcome == LlmOutcome.RateLimitedMinute && state.TheNextRateLimitExhaustsTheDay)
                outcome = LlmOutcome.QuotaExhaustedDay;

            // Rule 5: every call is recorded, whatever its outcome. No retry on the same link.
            await _usage.AddAsync(
                AiUsage.Record(_clock.UtcNow, link.Provider, link.Model, coupleId, request.Feature, result.InputTokens, result.OutputTokens, outcome.ToString(), result.LatencyMs),
                ct);
            _logger.LogInformation(
                "AI call {Feature} {Provider}|{Model}: {Outcome} in {LatencyMs} ms ({ErrorCode}).",
                request.Feature, link.Provider, link.Model, outcome, result.LatencyMs, result.ErrorCode ?? "-");

            if (outcome != LlmOutcome.Ok) continue;
            if (accept is null || accept(value!)) return new LlmGatewayResult<T>(LlmGatewayOutcome.Ok, value, link.Provider, link.Model);

            // Rejected by the caller's validators: the next link gets one chance.
            if (++rejections >= 2) break;
        }

        // Rule 6.
        return Stop<T>(rejections > 0 ? LlmGatewayOutcome.OutputRejected : LlmGatewayOutcome.AllProvidersFailed, request);
    }

    private LlmGatewayResult<T> Stop<T>(LlmGatewayOutcome outcome, LlmRequest request)
        where T : class
    {
        _logger.LogInformation("AI call {Feature} not answered: {Outcome}.", request.Feature, outcome);
        return new LlmGatewayResult<T>(outcome, null, null, null);
    }

    /// <summary>
    /// Rule 3. Interactive use: the group's budget of the Brasília day, then the ceiling of the whole app.
    /// The weekly and monthly jobs: only their own ceiling. All counted in ai_usage.
    /// </summary>
    private async Task<LlmGatewayOutcome?> BudgetOutcomeAsync(Guid? coupleId, string feature, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(_clock.UtcNow));

        if (LlmFeatures.IsJob(feature))
        {
            var jobs = await _usage.CountDayAsync(today, LlmFeatures.Jobs, ct);
            return jobs >= _options.JobDailyCalls ? LlmGatewayOutcome.GlobalBudgetExhausted : null;
        }

        if (coupleId is { } couple)
        {
            var group = await _usage.GetGroupDayAsync(couple, today, LlmFeatures.Interactive, ct);
            if (group.Calls >= _options.GroupDailyCalls || group.Tokens >= _options.GroupDailyTokens)
                return LlmGatewayOutcome.GroupBudgetExhausted;
        }

        var everyone = await _usage.CountDayAsync(today, LlmFeatures.Interactive, ct);
        return everyone >= _options.GlobalDailyInteractiveCalls ? LlmGatewayOutcome.GlobalBudgetExhausted : null;
    }

    /// <summary>A known daily limit of the provider (its day is UTC) is used up to 90%; an unknown one blocks nothing.</summary>
    private async Task<bool> WouldExceedTheDayAsync(LlmLink link, AiLimit limit, int estimatedTokens, DateTime now, CancellationToken ct)
    {
        if (limit.Rpd is null && limit.Tpd is null) return false;

        var day = await _usage.GetModelDayAsync(link.Provider, link.Model, DateOnly.FromDateTime(now), ct);
        if (limit.Rpd is { } rpd && day.Calls + 1 > AiOptions.Usable(rpd)) return true;
        return limit.Tpd is { } tpd && day.Tokens + estimatedTokens > AiOptions.Usable(tpd);
    }

    /// <summary>
    /// The minute window. Full: an interactive call skips the link; a job waits for it (up to the configured
    /// maximum) instead of falling to a worse model.
    /// </summary>
    private async Task<bool> ReserveMinuteAsync(LlmLink link, AiLimit limit, int estimatedTokens, LlmCallMode mode, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var wait = _window.TryReserve(link.Provider, link.Model, limit, estimatedTokens, _clock.UtcNow);
            if (wait <= TimeSpan.Zero) return true;
            if (mode == LlmCallMode.Interactive || waited + wait > _options.JobMaxWait) return false;

            await _waiter.DelayAsync(wait, ct);
            waited += wait;
        }
    }

    private async Task<LlmResult> CallAsync(ILlmProvider provider, LlmRequest request, TimeSpan timeout, CancellationToken ct)
    {
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkCts.CancelAfter(timeout);
        try
        {
            return await provider.GenerateAsync(request, linkCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmResult(LlmOutcome.Timeout, null, 0, 0, "TIMEOUT", (int)timeout.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: the message of an HTTP or JSON exception may quote what was sent or received.
            _logger.LogWarning("AI provider {Provider}|{Model} failed with {ExceptionType}.", provider.Provider, provider.Model, ex.GetType().Name);
            return new LlmResult(LlmOutcome.Error, null, 0, 0, "EXCEPTION", 0);
        }
    }

    /// <summary>Valid JSON, in the exact shape of the schema, that deserializes: anything else is InvalidOutput.</summary>
    private static bool TryRead<T>(string? json, LlmJsonSchema schema, out T? value)
        where T : class
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!schema.Matches(document.RootElement)) return false;
            value = document.RootElement.Deserialize<T>(JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
