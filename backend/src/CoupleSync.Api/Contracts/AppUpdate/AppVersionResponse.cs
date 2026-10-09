namespace CoupleSync.Api.Contracts.AppUpdate;

/// <summary>
/// <c>LatestVersion</c> and <c>MinimumVersion</c> are <c>X.Y.Z</c> or null (unknown / not configured);
/// <c>DownloadUrl</c> is always the fixed link of the latest APK.
/// </summary>
public sealed record AppVersionResponse(string? LatestVersion, string? MinimumVersion, string DownloadUrl);
