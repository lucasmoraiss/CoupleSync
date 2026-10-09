namespace CoupleSync.Application.Common.Interfaces;

/// <summary>Where the latest published release of the app is read from (the GitHub Releases of the repository).</summary>
public interface ILatestAppReleaseSource
{
    /// <summary>
    /// The tag of the latest release (e.g. <c>v1.1.0</c>), or null when it could not be read: the source being down,
    /// slow or answering something unexpected is never an error for the caller.
    /// </summary>
    Task<string?> GetLatestTagAsync(CancellationToken ct);
}
