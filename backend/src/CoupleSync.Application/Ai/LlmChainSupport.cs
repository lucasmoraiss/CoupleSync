using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;

namespace CoupleSync.Application.Ai;

/// <summary>
/// The Assistant's history as it is sent (design 3.9): from the most recent message to the oldest, whole messages,
/// up to 1,500 estimated tokens. The request validator still accepts 20 messages of 2,000 characters — the installed
/// app does not change — and the server cuts.
/// </summary>
public static class ChatHistoryTrimmer
{
    public const int MaxHistoryTokens = 1500;

    public static IReadOnlyList<LlmMessage> Trim(IReadOnlyList<LlmMessage> history, int maxTokens = MaxHistoryTokens)
    {
        var kept = new List<LlmMessage>();
        var used = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var cost = PromptText.EstimateTokens(history[i].Text);
            // Stops at the first that does not fit: an older message never takes the place of a newer one.
            if (used + cost > maxTokens) break;
            used += cost;
            kept.Add(history[i]);
        }

        kept.Reverse();
        return kept;
    }
}

/// <summary>
/// Requests and tokens of the last minute, per model. In memory on purpose (design 2.5): a minute is shorter than a
/// restart, and the daily numbers — the ones that matter after the API sleeps — come from ai_usage. Singleton.
/// </summary>
public sealed class LlmMinuteWindow
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<(DateTime At, long Tokens)>> _sent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reserves room for one request now. Returns zero when it was reserved; otherwise how long until there is room
    /// (nothing is reserved).
    /// </summary>
    public TimeSpan TryReserve(string provider, string model, AiLimit limit, long tokens, DateTime nowUtc)
    {
        if (limit.Rpm is null && limit.Tpm is null) return TimeSpan.Zero;

        lock (_gate)
        {
            var key = $"{provider}|{model}";
            if (!_sent.TryGetValue(key, out var sent)) _sent[key] = sent = new List<(DateTime, long)>();
            sent.RemoveAll(s => nowUtc - s.At >= Window);

            var wait = TimeSpan.Zero;
            if (limit.Rpm is { } rpm)
            {
                var usable = (int)AiOptions.Usable(rpm);
                if (sent.Count >= usable) wait = Max(wait, sent[sent.Count - usable].At + Window - nowUtc);
            }

            if (limit.Tpm is { } tpm)
            {
                var usable = AiOptions.Usable(tpm);
                var total = sent.Sum(s => s.Tokens) + tokens;
                // Oldest first: the window has room when enough of them have left it.
                for (var i = 0; i < sent.Count && total > usable; i++)
                {
                    total -= sent[i].Tokens;
                    wait = Max(wait, sent[i].At + Window - nowUtc);
                }
            }

            if (wait > TimeSpan.Zero) return wait;

            sent.Add((nowUtc, tokens));
            return TimeSpan.Zero;
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>
/// Where a link stands after its 429s, computed from its rows of ai_usage (design 2.5) — never from memory, so the
/// state survives the API going to sleep.
/// </summary>
public sealed record LlmLinkState(bool ExhaustedToday, DateTime? PausedUntilUtc, int RateLimitsInARow)
{
    /// <summary>How far back the rows are read: a pause is at most 30 minutes and an exhausted link returns at the next UTC day.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    public const int MaxEvents = 16;

    /// <summary>Pause after the 1st, 2nd and 3rd 429 in a row; the 4th takes the link out for the day.</summary>
    private static readonly TimeSpan[] Pauses = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    public bool TheNextRateLimitExhaustsTheDay => RateLimitsInARow >= Pauses.Length;

    public bool IsAvailable(DateTime nowUtc) => !ExhaustedToday && (PausedUntilUtc is null || nowUtc >= PausedUntilUtc);

    /// <param name="newestFirst">The Ok / RateLimitedMinute / QuotaExhaustedDay rows of the link, newest first.</param>
    public static LlmLinkState From(IReadOnlyList<AiLinkEvent> newestFirst, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        var exhausted = newestFirst.Any(e => e.Outcome == nameof(LlmOutcome.QuotaExhaustedDay) && e.DayUtc == today);

        // 429s in a row with no Ok in between; an Ok — or the end of a day the link was out of — starts a new run.
        var inARow = 0;
        DateTime? last = null;
        foreach (var e in newestFirst)
        {
            if (e.Outcome != nameof(LlmOutcome.RateLimitedMinute)) break;
            inARow++;
            last ??= e.CreatedAtUtc;
        }

        DateTime? pausedUntil = last is null ? null : last.Value + Pauses[Math.Min(inARow, Pauses.Length) - 1];
        return new LlmLinkState(exhausted, pausedUntil, inARow);
    }
}

/// <summary>Phase 1: there is no consent table yet; the chat is still behind the acceptance kept on the device.</summary>
public sealed class DeviceAiConsentGate : IAiConsentGate
{
    public Task<bool> IsEnabledAsync(Guid? coupleId, CancellationToken ct) => Task.FromResult(true);
}

public sealed class SystemLlmWaiter : ILlmWaiter
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
