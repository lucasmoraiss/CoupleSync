using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Domain.Entities;

/// <summary>
/// One call to an AI provider: accounting only (who, which model, how many tokens, how it ended). It never holds the
/// prompt nor the answer. The daily counters of the chain are COUNT/SUM over these rows, so they survive the API
/// going to sleep. Not <c>ICoupleScoped</c> (there are calls without a group): every read by group names the group.
/// </summary>
public sealed class AiUsage
{
    public const int MaxProviderLength = 40;
    public const int MaxModelLength = 80;
    public const int MaxFeatureLength = 40;
    public const int MaxOutcomeLength = 40;

    private AiUsage()
    {
    }

    public Guid Id { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The provider's day (UTC): the day its quota belongs to.</summary>
    public DateOnly DayUtc { get; private set; }

    /// <summary>The Brasília day: the day the group's budget and the global ceilings belong to.</summary>
    public DateOnly DayBrt { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public Guid? CoupleId { get; private set; }

    public string Feature { get; private set; } = string.Empty;

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public int LatencyMs { get; private set; }

    /// <summary>
    /// Only in a 429 whose answer said how long to wait: the instant from which the model can be called again.
    /// It is what keeps a paused or exhausted model out for exactly the time the provider asked, across restarts.
    /// </summary>
    public DateTime? RetryAtUtc { get; private set; }

    public static AiUsage Record(
        DateTime nowUtc,
        string provider,
        string model,
        Guid? coupleId,
        string feature,
        int inputTokens,
        int outputTokens,
        string outcome,
        int latencyMs,
        DateTime? retryAtUtc = null)
    {
        var utc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return new AiUsage
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = utc,
            DayUtc = DateOnly.FromDateTime(utc),
            DayBrt = DateOnly.FromDateTime(BrazilTime.ToLocal(utc)),
            Provider = Cut(provider, MaxProviderLength),
            Model = Cut(model, MaxModelLength),
            CoupleId = coupleId,
            Feature = Cut(feature, MaxFeatureLength),
            InputTokens = Math.Max(0, inputTokens),
            OutputTokens = Math.Max(0, outputTokens),
            Outcome = Cut(outcome, MaxOutcomeLength),
            LatencyMs = Math.Max(0, latencyMs),
            RetryAtUtc = retryAtUtc is { } retry ? DateTime.SpecifyKind(retry, DateTimeKind.Utc) : null,
        };
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
