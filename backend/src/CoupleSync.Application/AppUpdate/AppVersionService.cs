using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.AppUpdate;

/// <summary>What the app needs to decide whether to offer (or demand) a newer APK.</summary>
public sealed record AppVersionInfo(string? LatestVersion, string? MinimumVersion, string DownloadUrl);

/// <summary>
/// Latest version: the tag of the latest release. Minimum version: configuration. Either may be unknown (null), and
/// then the app simply shows nothing: a missing variable or a failing lookup is never an error here.
/// </summary>
public sealed class AppVersionService
{
    private readonly ILatestAppReleaseSource _latestRelease;
    private readonly AppUpdateOptions _options;

    public AppVersionService(ILatestAppReleaseSource latestRelease, IOptions<AppUpdateOptions> options)
    {
        _latestRelease = latestRelease;
        _options = options.Value;
    }

    public async Task<AppVersionInfo> GetAsync(CancellationToken ct)
    {
        var latestTag = await _latestRelease.GetLatestTagAsync(ct);

        return new AppVersionInfo(
            AppVersionNumber.Normalize(latestTag),
            AppVersionNumber.Normalize(_options.MinimumVersion),
            AppUpdateOptions.DownloadUrl);
    }
}
