namespace CoupleSync.Domain.Interfaces;

/// <summary>
/// Abstracts file storage. Returns the internal storage path — never expose this to API clients.
/// </summary>
public interface IStorageAdapter
{
    /// <summary>
    /// Uploads content and returns the relative storage path (internal use only).
    /// </summary>
    Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct);

    /// <summary>
    /// Downloads content from the given storage path. Caller is responsible for disposing the stream.
    /// </summary>
    Task<Stream> DownloadAsync(string storagePath, CancellationToken ct);

    /// <summary>
    /// Deletes the file at the given storage path. No-op if the file does not exist.
    /// </summary>
    Task DeleteAsync(string storagePath, CancellationToken ct);
}
