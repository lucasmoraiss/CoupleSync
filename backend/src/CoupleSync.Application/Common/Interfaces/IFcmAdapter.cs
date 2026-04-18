namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// Abstraction for sending push notifications via FCM.
/// Returns true on success, false on transient/permanent failure that should not crash the worker,
/// and throws on unrecoverable errors.
/// </summary>
public interface IFcmAdapter
{
    /// <summary>
    /// Sends a push notification to the specified device token.
    /// </summary>
    /// <returns>true on success; false on permanent/transient send failure (token invalid, etc.)</returns>
    Task<bool> SendAsync(string deviceToken, string title, string body, CancellationToken ct);
}
