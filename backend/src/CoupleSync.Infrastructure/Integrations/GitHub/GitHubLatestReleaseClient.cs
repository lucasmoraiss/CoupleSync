using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.GitHub;

/// <summary>
/// Reads the latest release of the app from the GitHub API (public repository: no token is sent). The answer is kept
/// in memory for an hour; a failure (GitHub down, slow, rate limited, unexpected body) is kept for a few minutes, so
/// that the API neither hammers GitHub nor makes every app wait for the timeout. While the lookup keeps failing the
/// last good tag goes on being the answer (for a day at most), so that the notice in the app does not come and go
/// with GitHub's limit of requests per address. Nothing here ever throws to the caller except its own cancellation.
/// </summary>
public sealed class GitHubLatestReleaseClient : ILatestAppReleaseSource, IDisposable
{
    public const string HttpClientName = "GitHubReleases";
    public const string UserAgent = "CoupleSync-API";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);
    /// <summary>How long the last good tag still answers while every new lookup fails.</summary>
    public static readonly TimeSpan LastGoodLifetime = TimeSpan.FromHours(24);

    /// <summary>The answer of GitHub for one release is a few kilobytes; anything much larger is not read.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private const string CacheKey = "app-update:latest-release-tag";
    private const string LastGoodCacheKey = "app-update:latest-release-tag:last-good";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly AppUpdateOptions _options;
    private readonly ILogger<GitHubLatestReleaseClient> _logger;
    // One lookup at a time: requests that arrive together wait for the first answer instead of each calling GitHub.
    private readonly SemaphoreSlim _lookup = new(1, 1);

    public GitHubLatestReleaseClient(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<AppUpdateOptions> options,
        ILogger<GitHubLatestReleaseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GetLatestTagAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out CachedTag? cached) && cached is not null) return cached.Tag;

        await _lookup.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(CacheKey, out cached) && cached is not null) return cached.Tag;

            var tag = await FetchAsync(ct);
            if (tag is not null)
            {
                _cache.Set(CacheKey, new CachedTag(tag), SuccessLifetime);
                _cache.Set(LastGoodCacheKey, new CachedTag(tag), LastGoodLifetime);
                return tag;
            }

            // Failed: for a few minutes the answer is the last good tag, if there is one that is not too old.
            var lastGood = _cache.TryGetValue(LastGoodCacheKey, out CachedTag? kept) ? kept?.Tag : null;
            _cache.Set(CacheKey, new CachedTag(lastGood), FailureLifetime);
            return lastGood;
        }
        finally
        {
            _lookup.Release();
        }
    }

    public void Dispose() => _lookup.Dispose();

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        var address = _options.LatestReleaseUrl?.Trim();
        if (string.IsNullOrEmpty(address)) return null;
        // Only http(s): HttpClient throws NotSupportedException for any other absolute address ("host:443/...", ftp, file).
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            _logger.LogWarning("App update: the address of the latest release is not a valid URL; no latest version.");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // GitHub refuses requests without a User-Agent.
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("App update: the latest release lookup answered {Status}; no latest version for now.", (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("tag_name", out var tagName)
                && tagName.ValueKind == JsonValueKind.String)
            {
                return tagName.GetString();
            }

            _logger.LogWarning("App update: the latest release lookup answered without a tag; no latest version for now.");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("App update: the latest release lookup timed out; no latest version for now.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning("App update: the latest release lookup failed ({Reason}); no latest version for now.", ex.GetType().Name);
            return null;
        }
    }

    private sealed record CachedTag(string? Tag);
}
