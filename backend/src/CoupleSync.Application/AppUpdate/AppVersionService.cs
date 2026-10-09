using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.AppUpdate;

/// <summary>What the app needs to decide whether to offer (or demand) a newer APK.</summary>
public sealed record AppVersionInfo(string? LatestVersion, string? MinimumVersion, string DownloadUrl);

/// <summary>
/// Remembers which invalid minimum version was last written to the log, so that the route (asked up to 30 times a
/// minute per address) says it once per state and not on every request. One for the whole application.
/// </summary>
public sealed class InvalidMinimumVersionNotice
{
    private readonly object _gate = new();
    private (string Minimum, string Latest)? _logged;

    /// <summary>True the first time this pair is seen since the configuration was last valid (or since start-up).</summary>
    public bool ShouldLog(string minimum, string latest)
    {
        lock (_gate)
        {
            if (_logged == (minimum, latest)) return false;
            _logged = (minimum, latest);
            return true;
        }
    }

    /// <summary>The configuration is valid now: if it becomes invalid again, that is said again.</summary>
    public void Clear()
    {
        lock (_gate) _logged = null;
    }
}

/// <summary>
/// Latest version: the tag of the latest release. Minimum version: configuration. Either may be unknown (null), and
/// then the app simply shows nothing: a missing variable or a failing lookup is never an error here.
/// The minimum version is what locks an installed app out, so it is only answered when an APK that unlocks it is
/// known to exist: never above the latest published version, and never while the latest is unknown.
/// </summary>
public sealed class AppVersionService
{
    private readonly ILatestAppReleaseSource _latestRelease;
    private readonly AppUpdateOptions _options;
    private readonly InvalidMinimumVersionNotice _invalidMinimumNotice;
    private readonly ILogger<AppVersionService> _logger;

    public AppVersionService(
        ILatestAppReleaseSource latestRelease,
        IOptions<AppUpdateOptions> options,
        InvalidMinimumVersionNotice invalidMinimumNotice,
        ILogger<AppVersionService> logger)
    {
        _latestRelease = latestRelease;
        _options = options.Value;
        _invalidMinimumNotice = invalidMinimumNotice;
        _logger = logger;
    }

    public async Task<AppVersionInfo> GetAsync(CancellationToken ct)
    {
        var latest = AppVersionNumber.Normalize(await _latestRelease.GetLatestTagAsync(ct));
        var minimum = AppVersionNumber.Normalize(_options.MinimumVersion);

        if (minimum is not null && latest is null)
        {
            // Not a configuration error (the lookup failed): nobody is locked out until the latest is known again.
            minimum = null;
        }
        else if (minimum is not null && AppVersionNumber.Compare(minimum, latest) > 0)
        {
            // Once per state, not on every request of a route that is asked many times a minute.
            if (_invalidMinimumNotice.ShouldLog(minimum, latest!))
            {
                _logger.LogWarning(
                    "App update: invalid configuration, {Variable}={Minimum} is above the latest published version {Latest}; ignored, nobody is blocked.",
                    AppUpdateOptions.MinimumVersionVariable, minimum, latest);
            }

            minimum = null;
        }
        else
        {
            _invalidMinimumNotice.Clear();
        }

        return new AppVersionInfo(latest, minimum, AppUpdateOptions.DownloadUrl);
    }
}
