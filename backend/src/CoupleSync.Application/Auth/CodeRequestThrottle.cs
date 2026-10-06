using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Auth;

/// <summary>
/// Caps how many codes can be e-mailed to one address (per purpose) in a window, so the endpoints cannot be
/// used to flood a victim's inbox from many IPs. Keyed by the address whether or not an account exists, so
/// hitting the cap reveals nothing. In memory: one API instance runs today, and a restart only resets the budget.
/// </summary>
public sealed class CodeRequestThrottle
{
    public const int MaxRequestsPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private const int PruneThreshold = 5000;

    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime WindowStartUtc, int Count)> _entries = new();

    public CodeRequestThrottle(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    /// <summary>Spends one request for this purpose and address; false when the window's budget is gone.</summary>
    public bool TryAcquire(string purpose, string email)
    {
        var key = $"{purpose}:{email.Trim().ToLowerInvariant()}";
        var now = _dateTimeProvider.UtcNow;

        lock (_gate)
        {
            if (_entries.Count > PruneThreshold)
            {
                foreach (var expired in _entries.Where(x => now - x.Value.WindowStartUtc >= Window).Select(x => x.Key).ToList())
                {
                    _entries.Remove(expired);
                }
            }

            if (!_entries.TryGetValue(key, out var entry) || now - entry.WindowStartUtc >= Window)
            {
                _entries[key] = (now, 1);
                return true;
            }

            if (entry.Count >= MaxRequestsPerWindow)
            {
                return false;
            }

            _entries[key] = (entry.WindowStartUtc, entry.Count + 1);
            return true;
        }
    }
}
