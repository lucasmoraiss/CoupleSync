using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>How a call behaves when a link is busy. The budget a call counts in comes from its feature, not from this.</summary>
public enum LlmCallMode
{
    /// <summary>A route of the app: a busy link is skipped; the whole call has 20 s.</summary>
    Interactive,

    /// <summary>Background work: waits for the minute window of the better model; 60 s per link.</summary>
    Job,
}

public enum LlmGatewayOutcome
{
    Ok,

    /// <summary>The group has not switched the AI on.</summary>
    NotConsented,

    /// <summary>Emergency switch (<c>Ai__Disabled</c>).</summary>
    Disabled,

    /// <summary>The group used its interactive budget of the Brasília day.</summary>
    GroupBudgetExhausted,

    /// <summary>The ceiling of the day for the whole app was reached (interactive use, or jobs).</summary>
    GlobalBudgetExhausted,

    AllProvidersFailed,

    /// <summary>A model answered, but the caller's validators rejected the answer twice.</summary>
    OutputRejected,
}

public sealed record LlmGatewayResult<T>(LlmGatewayOutcome Outcome, T? Value, string? Provider, string? Model)
    where T : class;

/// <summary>The chain: this is what the services use. No service talks to a provider directly.</summary>
public interface ILlmGateway
{
    Task<LlmGatewayResult<T>> GenerateAsync<T>(Guid? coupleId, LlmRequest request, LlmCallMode mode, CancellationToken ct)
        where T : class;

    /// <summary>
    /// The same, with the caller's validators: an answer that <paramref name="accept"/> rejects sends the chain to the
    /// next link once; rejected again, the result is <see cref="LlmGatewayOutcome.OutputRejected"/>.
    /// </summary>
    Task<LlmGatewayResult<T>> GenerateAsync<T>(Guid? coupleId, LlmRequest request, LlmCallMode mode, Func<T, bool>? accept, CancellationToken ct)
        where T : class;
}

/// <summary>The providers that can be called now: those with a key (or the fake one, when it is on).</summary>
public interface ILlmProviderCatalog
{
    /// <summary>Null when the provider is unknown or has no key: such a link is in no chain.</summary>
    ILlmProvider? Find(string provider, string model);

    bool AnyAvailable { get; }
}

/// <summary>
/// "Has this group switched the AI on?" Phase 1 has no consent table yet (the chat is still behind the acceptance
/// stored on the device), so the registered implementation says yes; phase 2 replaces it.
/// </summary>
public interface IAiConsentGate
{
    Task<bool> IsEnabledAsync(Guid? coupleId, CancellationToken ct);
}

/// <summary>Waiting for a minute window, behind an interface so that tests do not wait for real.</summary>
public interface ILlmWaiter
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed record AiUsageTotals(int Calls, long Tokens);

public sealed record AiLinkEvent(DateTime CreatedAtUtc, DateOnly DayUtc, string Outcome);

/// <summary>A member of the group as the AI sees it: a letter. The name never leaves the API.</summary>
/// <param name="Marker">"A", "B"... by order of joining the group.</param>
public sealed record AiPerson(string Marker, string FullName);

public interface IAiPeopleReader
{
    /// <summary>The members of the group, oldest membership first, as A, B, C...</summary>
    Task<IReadOnlyList<AiPerson>> GetPeopleAsync(Guid coupleId, CancellationToken ct);
}

/// <summary>ai_usage has no global group filter: every method that reads by group takes the group.</summary>
public interface IAiUsageRepository
{
    /// <summary>Stores the row at once.</summary>
    Task AddAsync(AiUsage usage, CancellationToken ct);

    /// <summary>Calls that spent tokens, and the tokens, of one group in one Brasília day, in the given features.</summary>
    Task<AiUsageTotals> GetGroupDayAsync(Guid coupleId, DateOnly dayBrt, IReadOnlyCollection<string> features, CancellationToken ct);

    /// <summary>Calls that spent tokens in one Brasília day, of every group together, in the given features.</summary>
    Task<int> CountDayAsync(DateOnly dayBrt, IReadOnlyCollection<string> features, CancellationToken ct);

    /// <summary>Requests sent to one model in one day of the provider (UTC), and the tokens they spent.</summary>
    Task<AiUsageTotals> GetModelDayAsync(string provider, string model, DateOnly dayUtc, CancellationToken ct);

    /// <summary>The latest Ok / 429 rows of one model since an instant, newest first.</summary>
    Task<IReadOnlyList<AiLinkEvent>> GetRecentLinkEventsAsync(string provider, string model, DateTime sinceUtc, int take, CancellationToken ct);
}
