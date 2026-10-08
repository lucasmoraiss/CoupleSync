using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Pluggy;

/// <summary>
/// HTTP client of the Pluggy API. The API key of each connection is kept in memory for 110 minutes (Pluggy's lasts
/// 120), together with a fingerprint of the stored credentials it was obtained with: a key of credentials that were
/// erased or replaced is never used again. A 401 on a data call drops it and the call is repeated once with a new key. Nothing sensitive is logged:
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

    /// <summary>The largest page Pluggy gives.</summary>
    public const int TransactionsPageSize = 500;

    /// <summary>A ceiling for a listing that never says it ended (500 000 transactions of one account).</summary>
    private const int MaxTransactionPages = 1000;

    public async Task<IReadOnlyList<PluggyTransaction>> GetTransactionsAsync(PluggyAuth auth, string accountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var transactions = new List<PluggyTransaction>();
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/transactions?accountId={Uri.EscapeDataString(accountId)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&pageSize={TransactionsPageSize}");

        for (var page = 1; page <= MaxTransactionPages; page++)
        {
            using var json = await SendAsync(
                auth, HttpMethod.Get, "transactions", string.Create(CultureInfo.InvariantCulture, $"{query}&page={page}"), notFoundMeansNoItem: true, ct);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw Unavailable("transactions", "unexpected body");
            }

            var received = 0;
            foreach (var element in results.EnumerateArray())
            {
                received++;
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = Text(element, "id");
                var date = Instant(element, "date");
                var amount = Number(element, "amount");
                // Without an id, a date or a value there is nothing to mirror.
                if (id is null || date is null || amount is null) continue;

                var merchant = Child(element, "merchant");
                var payment = Child(element, "paymentData");
                var card = Child(element, "creditCardMetadata");
                transactions.Add(new PluggyTransaction(
                    id,
                    date.Value,
                    amount.Value,
                    Text(element, "type"),
                    Text(element, "currencyCode"),
                    Text(element, "description"),
                    Text(element, "descriptionRaw"),
                    Text(element, "category"),
                    Text(element, "categoryId"),
                    merchant is { } named ? Text(named, "name") ?? Text(named, "businessName") : null,
                    merchant is { } registered ? Text(registered, "cnpj") : null,
                    merchant is { } classified ? Text(classified, "category") : null,
                    payment is { } paid ? Text(paid, "paymentMethod") : null,
                    card is { } installment ? Whole(installment, "installmentNumber") : null,
                    card is { } installments ? Whole(installments, "totalInstallments") : null,
                    card is { } billed ? Text(billed, "billId") : null,
                    Text(element, "status"),
                    Number(element, "balance"),
                    element.GetRawText()));
            }

            var totalPages = Whole(root, "totalPages");
            if (received == 0 || (totalPages is { } last && page >= last)) break;
            // No page count in the answer: a page that is not full is the last one.
            if (totalPages is null && received < TransactionsPageSize) break;
        }

        return transactions;
    }

    public async Task RequestItemUpdateAsync(PluggyAuth auth, string itemId, CancellationToken ct)
    {
        using var json = await SendAsync(
            auth, HttpMethod.Patch, "item-update", $"/items/{Uri.EscapeDataString(itemId)}", notFoundMeansNoItem: true, ct);
    }

    public void ForgetConnection(Guid connectionId) => _cache.Remove(CacheKey(connectionId));

    // ------------------------------------------------------------------ HTTP

    private static string CacheKey(Guid connectionId) => $"pluggy:api-key:{connectionId:N}";

    private string Url(string pathAndQuery) => _options.PluggyBaseUrl.TrimEnd('/') + pathAndQuery;

    private async Task<string> GetApiKeyAsync(PluggyAuth auth, CancellationToken ct)
    {
        if (auth.ConnectionId is not { } connectionId)
            return await RequestApiKeyAsync(auth.ClientId, auth.ClientSecret, ct);

        // The key is kept with the fingerprint of the stored credentials it came from: after a disconnection (or a
        // new connection) the stored secret is another text, and the key of the old credentials is not this one.
        var credentials = CredentialsFingerprint(auth);
        if (_cache.TryGetValue(CacheKey(connectionId), out CachedApiKey? cached)
            && cached is not null
            && string.Equals(cached.Credentials, credentials, StringComparison.Ordinal))
        {
            return cached.ApiKey;
        }

        var apiKey = await RequestApiKeyAsync(auth.ClientId, auth.ClientSecret, ct);
        _cache.Set(CacheKey(connectionId), new CachedApiKey(credentials, apiKey), ApiKeyLifetime);
        return apiKey;
    }

    private static string CredentialsFingerprint(PluggyAuth auth)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth.CredentialsVersion ?? string.Empty)));

    private sealed record CachedApiKey(string Credentials, string ApiKey)
    {
        public override string ToString() => nameof(CachedApiKey);
    }

    private async Task<string> RequestApiKeyAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/auth"));
        // Serialised first, so the body goes out whole with a Content-Length (JsonContent would send it in chunks).
        request.Content = new StringContent(JsonSerializer.Serialize(new { clientId, clientSecret }), Encoding.UTF8, "application/json");

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

    private Task<JsonDocument> GetAsync(PluggyAuth auth, string kind, string pathAndQuery, bool notFoundMeansNoItem, CancellationToken ct)
        => SendAsync(auth, HttpMethod.Get, kind, pathAndQuery, notFoundMeansNoItem, ct);

    /// <summary>A call with the connection's API key; a 401 drops the key and repeats the call once with a new one.</summary>
    private async Task<JsonDocument> SendAsync(
        PluggyAuth auth, HttpMethod method, string kind, string pathAndQuery, bool notFoundMeansNoItem, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var apiKey = await GetApiKeyAsync(auth, ct);
            using var request = new HttpRequestMessage(method, Url(pathAndQuery));
            request.Headers.Add(ApiKeyHeader, apiKey);
            if (method != HttpMethod.Get)
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

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

    private static JsonElement? Child(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    /// <summary>A whole number (a JSON number without a fraction), or null.</summary>
    private static int? Whole(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var whole)
            ? whole
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
