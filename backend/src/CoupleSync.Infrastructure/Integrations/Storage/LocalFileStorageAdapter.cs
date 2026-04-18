using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Infrastructure.Integrations.Storage;

/// <summary>
/// Local filesystem storage adapter for the pilot (≤10 users).
/// Implements IStorageAdapter; swap for a cloud adapter when deploying to production.
/// Storage:BasePath config key controls the root directory.
/// </summary>
public sealed class LocalFileStorageAdapter : IStorageAdapter
{
    private readonly string _basePath;
    private readonly ILogger<LocalFileStorageAdapter> _logger;

    public LocalFileStorageAdapter(IConfiguration config, ILogger<LocalFileStorageAdapter> logger)
    {
        _basePath = config["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _logger = logger;
    }

    public async Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
    {
        var ext = mimeType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "application/pdf" => "pdf",
            _ => "bin"
        };

        var relativePath = $"uploads/{coupleId}/{uploadId}.{ext}";
        var fullPath = Path.Combine(_basePath, coupleId.ToString(), $"{uploadId}.{ext}");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(fileStream, ct);

        _logger.LogInformation("Stored upload {UploadId} for couple {CoupleId} at {Path}", uploadId, coupleId, fullPath);

        return relativePath;
    }

    public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
    {
        // Reject absolute paths before any path combination (OWASP path traversal guard)
        if (Path.IsPathRooted(storagePath))
            throw new UnauthorizedAccessException("Access to the requested path is denied.");

        // storagePath is "uploads/{coupleId}/{uploadId}.ext"
        // Strip the leading "uploads/" prefix before combining with _basePath
        var suffix = storagePath.StartsWith("uploads/", StringComparison.Ordinal)
            ? storagePath["uploads/".Length..]
            : storagePath;

        var fullPath = Path.Combine(_basePath, suffix);

        // Canonicalization check: resolved path must remain inside _basePath
        var canonicalBase = Path.GetFullPath(_basePath);
        var canonicalFull = Path.GetFullPath(fullPath);
        if (!canonicalFull.StartsWith(canonicalBase + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Access to the requested path is denied.");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Stored file not found at resolved path.");

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }
}
