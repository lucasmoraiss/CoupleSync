namespace CoupleSync.Application.Common.Options;

/// <summary>
/// What the API tells the installed app about newer APKs. The <c>AppUpdate</c> section holds the address of the
/// lookup; the minimum version comes only from the <see cref="MinimumVersionVariable"/> environment variable.
/// </summary>
public sealed class AppUpdateOptions
{
    public const string SectionName = "AppUpdate";

    /// <summary>Environment variable (and configuration key) with the oldest app version still accepted, e.g. <c>1.1.0</c>.</summary>
    public const string MinimumVersionVariable = "APP_MINIMUM_VERSION";

    /// <summary>Fixed address of the APK of the latest release: the same link whatever the version.</summary>
    public const string DownloadUrl = "https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk";

    public const string DefaultLatestReleaseUrl = "https://api.github.com/repos/lucasmoraiss/CoupleSync/releases/latest";

    /// <summary>Where the latest release is read from (public repository, no token). Empty: no lookup, no latest version.</summary>
    public string LatestReleaseUrl { get; set; } = DefaultLatestReleaseUrl;

    /// <summary>Absent or not a version: there is no minimum version, and nobody is blocked.</summary>
    public string MinimumVersion { get; set; } = string.Empty;
}
