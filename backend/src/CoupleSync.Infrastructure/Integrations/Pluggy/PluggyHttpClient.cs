using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Pluggy;

/// <summary>
/// HTTP client of the Pluggy API. The API key of each connection is kept in memory for 110 minutes (Pluggy's lasts
/// 120); a 401 on a data call drops it and the call is repeated once with a new key. Nothing sensitive is logged:
/// only the route kind and the status. Numbers and dates are read culture-independently (JSON tokens, ISO 8601).
/// </summary>
public sealed class PluggyHttpClient : IPluggyClient
{
    public const string HttpClientName = "Pluggy";
    public const string ApiKeyHeader = "X-API-KEY";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ApiKeyLifetime = TimeSpan.FromMinutes(110);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly OpenFinanceOptions _options;
    private readonly ILogger<PluggyHttpClient> _logger;

    public PluggyHttpClient(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<OpenFinanceOptions> options,
        ILogger<PluggyHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PluggyAuth> AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        await RequestApiKeyAsync(clientId, clientSecret, ct);
        return PluggyAuth.Of(clientId, clientSecret);
    }

    public async Task<PluggyItem> GetItemAsync(PluggyAuth auth, string itemId, CancellationToken ct)
    {
        using var json = await GetAsync(auth, "item", $"/items/{Uri.EscapeDataString(itemId)}", notFoundMeansNoItem: true, ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Unavailable("item", "unexpected body");

        var connector = root.TryGetProperty("connector", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "name") : null;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? Text(e, "message") : null;
        return new PluggyItem(
            Text(root, "id") ?? itemId,
            connector ?? string.Empty,
            Text(root, "status") ?? string.Empty,
            Text(root, "executionStatus"),
            Instant(root, "updatedAt"),
            error);
    }

    public async Task<IReadOnlyList<PluggyAccount>> GetAccountsAsync(PluggyAuth auth, string itemId, CancellationToken ct)
    {
        using var json = await GetAsync(auth, "accounts", $"/accounts?itemId={Uri.EscapeDataString(itemId)}", notFoundMeansNoItem: true, ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            throw Unavailable("accounts", "unexpected body");
        }

        var accounts = new List<PluggyAccount>();
        foreach (var element in results.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var id = Text(element, "id");
            if (id is null) continue;

            PluggyCreditData? credit = null;
            if (element.TryGetProperty("creditData", out var cd) && cd.ValueKind == JsonValueKind.Object)
            {
                credit = new PluggyCreditData(
                    Text(cd, "level"),
                    Text(cd, "brand"),
                    Day(cd, "balanceCloseDate"),
                    Day(cd, "balanceDueDate"),
                    Number(cd, "availableCreditLimit"),
                    Number(cd, "creditLimit"),
                    Number(cd, "minimumPayment"));
            }

            accounts.Add(new PluggyAccount(
                id,
                Text(element, "type") ?? string.Empty,
                Text(element, "subtype"),
                Text(element, "name") ?? string.Empty,
                Text(element, "marketingName"),
                Text(element, "number"),
                Number(element, "balance") ?? 0m,
                Text(element, "currencyCode"),
                credit));
        }

        return accounts;
    }

    public void ForgetConnection(Guid connectionId) => _cache.Remove(CacheKey(connectionId));

    // ------------------------------------------------------------------ HTTP

    private static string CacheKey(Guid connectionId) => $"pluggy:api-key:{connectionId:N}";

    private string Url(string pathAndQuery) => _options.PluggyBaseUrl.TrimEnd('/') + pathAndQuery;

    private async Task<string> GetApiKeyAsync(PluggyAuth auth, CancellationToken ct)
    {
        if (auth.ConnectionId is not { } connectionId)
            return await RequestApiKeyAsync(auth.ClientId, auth.ClientSecret, ct);

        if (_cache.TryGetValue(CacheKey(connectionId), out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var apiKey = await RequestApiKeyAsync(auth.ClientId, auth.ClientSecret, ct);
        _cache.Set(CacheKey(connectionId), apiKey, ApiKeyLifetime);
        return apiKey;
    }

    private async Task<string> RequestApiKeyAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/auth"));
        request.Content = JsonContent.Create(new { clientId, clientSecret });

        using var response = await SendAsync(request, "auth", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                // Pluggy answers 400 to a client id that is not even in its format.
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => new PluggyException(PluggyErrorCodes.InvalidCredentials),
                HttpStatusCode.TooManyRequests => new PluggyException(PluggyErrorCodes.RateLimited),
                _ => new PluggyException(PluggyErrorCodes.Unavailable),
            };
        }

        using var json = await ReadJsonAsync(response, "auth", ct);
        var apiKey = json.RootElement.ValueKind == JsonValueKind.Object ? Text(json.RootElement, "apiKey") : null;
        return apiKey ?? throw Unavailable("auth", "no apiKey in the answer");
    }

    /// <summary>A GET with the connection's API key; a 401 drops the key and repeats the call once with a new one.</summary>
    private async Task<JsonDocument> GetAsync(PluggyAuth auth, string kind, string pathAndQuery, bool notFoundMeansNoItem, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var apiKey = await GetApiKeyAsync(auth, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(pathAndQuery));
            request.Headers.Add(ApiKeyHeader, apiKey);

            using var response = await SendAsync(request, kind, ct);
            if (response.IsSuccessStatusCode)
                return await ReadJsonAsync(response, kind, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (auth.ConnectionId is { } connectionId) ForgetConnection(connectionId);
                if (attempt == 1) continue;
                throw new PluggyException(PluggyErrorCodes.InvalidCredentials);
            }

            throw response.StatusCode switch
            {
                HttpStatusCode.Forbidden => new PluggyException(PluggyErrorCodes.InvalidCredentials),
                // 400: an id that is not in Pluggy's format is an item Pluggy does not have either.
                HttpStatusCode.NotFound or HttpStatusCode.BadRequest when notFoundMeansNoItem
                    => new PluggyException(PluggyErrorCodes.ItemNotFound),
                HttpStatusCode.TooManyRequests => new PluggyException(PluggyErrorCodes.RateLimited),
                _ => new PluggyException(PluggyErrorCodes.Unavailable),
            };
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string kind, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Pluggy {Kind} call failed before an answer: {Error}.", kind, ex.HttpRequestError);
            throw new PluggyException(PluggyErrorCodes.Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not the caller giving up: the client's own timeout.
            _logger.LogWarning("Pluggy {Kind} call timed out.", kind);
            throw new PluggyException(PluggyErrorCodes.Unavailable);
        }

        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Pluggy {Kind} call answered {StatusCode}.", kind, (int)response.StatusCode);

        return response;
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string kind, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            throw Unavailable(kind, "unreadable body");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable(kind, "timed out reading the body");
        }
    }

    private PluggyException Unavailable(string kind, string reason)
    {
        _logger.LogWarning("Pluggy {Kind} call gave an unusable answer: {Reason}.", kind, reason);
        return new PluggyException(PluggyErrorCodes.Unavailable);
    }

    // ------------------------------------------------------------------ JSON (culture-independent)

    private static string? Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A JSON number, or a number written as text with a dot ("1234.56"). Never the host's culture.</summary>
    private static decimal? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetDecimal(out var number)) return number;
            // Outside decimal's range or precision (1e-30...): through double, still culture-free.
            return value.TryGetDouble(out var approximate) && double.IsFinite(approximate)
                   && Math.Abs(approximate) < (double)decimal.MaxValue
                ? (decimal)approximate
                : null;
        }

        return value.ValueKind == JsonValueKind.String
               && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? Instant(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    /// <summary>The calendar day of a Pluggy date ("2026-10-20T00:00:00.000Z" or "2026-10-20"), as written.</summary>
    private static DateOnly? Day(JsonElement parent, string name)
    {
        var text = Text(parent, name);
        if (text is null || text.Length < 10) return null;
        return DateOnly.TryParseExact(text.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }
}
