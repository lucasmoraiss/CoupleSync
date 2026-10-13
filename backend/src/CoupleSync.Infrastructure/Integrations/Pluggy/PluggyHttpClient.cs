using System.Diagnostics;
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
/// only the kind of the call, the status, how long it took, the page, how many entries came and the codes of Pluggy's
/// error body (never an address, an identifier, a cursor nor anything of an account or of a transaction). Numbers and
/// dates are read culture-independently (JSON tokens, ISO 8601).
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
        using var json = await SendAsync(auth, HttpMethod.Get, "item", $"/items/{Uri.EscapeDataString(itemId)}", ct);
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
        using var json = await SendAsync(auth, HttpMethod.Get, "accounts", $"/accounts?itemId={Uri.EscapeDataString(itemId)}", ct);
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

    /// <summary>A ceiling for a listing that never says it ended (Pluggy's page is of 500: 500 000 transactions of one account).</summary>
    public const int MaxTransactionPages = 1000;

    /// <summary>
    /// GET /v2/transactions, page after page by the cursor of "next", until "next" is null. Only <c>accountId</c>,
    /// <c>dateFrom</c>, <c>dateTo</c> and <c>after</c> are ever sent: the listing refuses what it does not know
    /// (<c>page</c>, <c>pageSize</c>, <c>from</c>, <c>to</c>). The listing ends early, and says it is not complete,
    /// when Pluggy gives a cursor already sent (it would go in circles), a "next" no cursor can be read from, or
    /// more pages than <see cref="MaxTransactionPages"/>.
    /// </summary>
    public async Task<PluggyTransactionListing> GetTransactionsAsync(PluggyAuth auth, string accountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var transactions = new List<PluggyTransaction>();
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/v2/transactions?accountId={Uri.EscapeDataString(accountId)}&dateFrom={from:yyyy-MM-dd}&dateTo={to:yyyy-MM-dd}");
        string? after = null;
        var sent = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 1; ; page++)
        {
            if (page > MaxTransactionPages)
            {
                _logger.LogWarning(
                    "Pluggy transactions listing reached the ceiling of {Pages} pages without an end; it is taken as not complete.", MaxTransactionPages);
                return new PluggyTransactionListing(transactions, Complete: false);
            }

            // 400 here is about the request (a parameter, a cursor), never about the item.
            using var json = await SendAsync(
                auth,
                HttpMethod.Get,
                "transactions",
                after is null ? query : $"{query}&after={Uri.EscapeDataString(after)}",
                ct,
                page,
                badRequestMeansNoItem: false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw Unavailable("transactions", "unexpected body");
            }

            foreach (var element in results.EnumerateArray())
            {
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
                    element.GetRawText(),
                    payment is { } transfer && Child(transfer, "payer") is { } payer ? Text(payer, "name") : null));
            }

            switch (ReadNext(root, out var cursor))
            {
                case NextPage.None:
                    return new PluggyTransactionListing(transactions, Complete: true);
                case NextPage.Unreadable:
                    _logger.LogWarning(
                        "Pluggy transactions listing gave a next page that could not be read on page {Page}; it is taken as not complete.", page);
                    return new PluggyTransactionListing(transactions, Complete: false);
            }

            if (!sent.Add(cursor!))
            {
                _logger.LogWarning(
                    "Pluggy transactions listing repeated a cursor on page {Page}; it is taken as not complete.", page);
                return new PluggyTransactionListing(transactions, Complete: false);
            }

            after = cursor;
        }
    }

    private enum NextPage
    {
        /// <summary>"next" is null (or empty): Pluggy says the listing ended.</summary>
        None,

        /// <summary>There is a next page and its cursor was read.</summary>
        Cursor,

        /// <summary>There may be a next page, and nothing tells how to ask for it.</summary>
        Unreadable,
    }

    private static NextPage ReadNext(JsonElement root, out string? cursor)
    {
        cursor = null;
        // The listing always carries "next": without it, nothing says that it ended.
        if (!root.TryGetProperty("next", out var next)) return NextPage.Unreadable;
        if (next.ValueKind == JsonValueKind.Null) return NextPage.None;
        if (next.ValueKind != JsonValueKind.String) return NextPage.Unreadable;

        var text = next.GetString()!.Trim();
        if (text.Length == 0) return NextPage.None;

        cursor = CursorOf(text);
        return cursor is null ? NextPage.Unreadable : NextPage.Cursor;
    }

    /// <summary>
    /// The cursor inside "next", in any of the forms it was seen in: the query string of the next page (documented:
    /// "?accountId=...&amp;after=..."), with or without the question mark; the whole address of the next page, absolute or
    /// relative; or the cursor alone. From a query string, only the value of <c>after</c>, URL-decoded; the cursor alone
    /// is taken as written. Null when there is no cursor to read.
    /// </summary>
    private static string? CursorOf(string next)
    {
        string query;
        var mark = next.IndexOf('?');
        if (mark >= 0)
            query = next[(mark + 1)..];
        else if (next.StartsWith("after=", StringComparison.Ordinal) || next.Contains('&'))
            query = next;
        else if (next.Contains("://", StringComparison.Ordinal))
            return null; // an address without a query string
        else
            return next;

        var fragment = query.IndexOf('#');
        if (fragment >= 0) query = query[..fragment];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals < 0 || !part.AsSpan(0, equals).SequenceEqual("after")) continue;
            var value = Uri.UnescapeDataString(part[(equals + 1)..]);
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>The page size asked of GET /bills (Pluggy's own default).</summary>
    public const int BillsPageSize = 500;

    /// <summary>A ceiling for a listing of bills that never says it ended.</summary>
    private const int MaxBillPages = 20;

    public async Task<IReadOnlyList<PluggyBill>> GetBillsAsync(PluggyAuth auth, string accountId, CancellationToken ct)
    {
        var bills = new List<PluggyBill>();
        var query = string.Create(CultureInfo.InvariantCulture, $"/bills?accountId={Uri.EscapeDataString(accountId)}&pageSize={BillsPageSize}");

        for (var page = 1; page <= MaxBillPages; page++)
        {
            using var json = await SendAsync(
                auth, HttpMethod.Get, "bills", string.Create(CultureInfo.InvariantCulture, $"{query}&page={page}"), ct, page);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw Unavailable("bills", "unexpected body");
            }

            var received = 0;
            foreach (var element in results.EnumerateArray())
            {
                received++;
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = Text(element, "id");
                // The day as the bank wrote it ("2026-10-27T00:00:00.000Z", "2026-10-27T00:00:00", "2026-10-27"):
                // a due date is a calendar day, never an instant to convert.
                var dueDate = Day(element, "dueDate");
                var total = Number(element, "totalAmount");
                // Without an id, a due date or a total there is nothing to mirror.
                if (id is null || dueDate is null || total is null) continue;

                bills.Add(new PluggyBill(
                    id,
                    dueDate.Value,
                    Day(element, "billClosingDate"),
                    total.Value,
                    Text(element, "totalAmountCurrencyCode"),
                    Number(element, "minimumPaymentAmount"),
                    element.GetRawText()));
            }

            var totalPages = Whole(root, "totalPages");
            if (received == 0 || (totalPages is { } last && page >= last)) break;
            // No page count in the answer: a page that is not full is the last one.
            if (totalPages is null && received < BillsPageSize) break;
        }

        return bills;
    }

    public async Task RequestItemUpdateAsync(PluggyAuth auth, string itemId, CancellationToken ct)
    {
        using var json = await SendAsync(auth, HttpMethod.Patch, "item-update", $"/items/{Uri.EscapeDataString(itemId)}", ct);
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

        const string kind = "auth";
        var watch = Stopwatch.StartNew();
        using var response = await SendAsync(request, kind, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var pluggyCode = await ReadErrorCodeAsync(response, ct);
            LogRefusal(kind, status, watch, page: null, pluggyCode);
            throw new PluggyException(
                response.StatusCode switch
                {
                    // Pluggy answers 400 to a client id that is not even in its format.
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PluggyErrorCodes.InvalidCredentials,
                    HttpStatusCode.TooManyRequests => PluggyErrorCodes.RateLimited,
                    _ => CodeOfUnexpected(response.StatusCode),
                },
                kind,
                status,
                pluggyCode);
        }

        using var json = await ReadJsonAsync(response, kind, ct);
        LogAnswer(kind, (int)response.StatusCode, watch, page: null, json.RootElement);
        var apiKey = json.RootElement.ValueKind == JsonValueKind.Object ? Text(json.RootElement, "apiKey") : null;
        return apiKey ?? throw Unavailable(kind, "no apiKey in the answer");
    }

    /// <summary>
    /// A call with the connection's API key; a 401 drops the key and repeats the call once with a new one.
    /// <paramref name="page"/> only goes to the log. <paramref name="badRequestMeansNoItem"/>: on the routes that
    /// carry an id of Pluggy's, a 400 is an id that is not in its format, so an item Pluggy does not have either.
    /// </summary>
    private async Task<JsonDocument> SendAsync(
        PluggyAuth auth, HttpMethod method, string kind, string pathAndQuery, CancellationToken ct, int? page = null, bool badRequestMeansNoItem = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            var apiKey = await GetApiKeyAsync(auth, ct);
            using var request = new HttpRequestMessage(method, Url(pathAndQuery));
            request.Headers.Add(ApiKeyHeader, apiKey);
            if (method != HttpMethod.Get)
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var watch = Stopwatch.StartNew();
            using var response = await SendAsync(request, kind, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await ReadJsonAsync(response, kind, ct);
                LogAnswer(kind, (int)response.StatusCode, watch, page, json.RootElement);
                return json;
            }

            var status = (int)response.StatusCode;
            var pluggyCode = await ReadErrorCodeAsync(response, ct);
            LogRefusal(kind, status, watch, page, pluggyCode);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (auth.ConnectionId is { } connectionId) ForgetConnection(connectionId);
                if (attempt == 1) continue;
                throw new PluggyException(PluggyErrorCodes.InvalidCredentials, kind, status, pluggyCode);
            }

            throw new PluggyException(
                response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => PluggyErrorCodes.InvalidCredentials,
                    HttpStatusCode.NotFound => PluggyErrorCodes.ItemNotFound,
                    HttpStatusCode.BadRequest when badRequestMeansNoItem => PluggyErrorCodes.ItemNotFound,
                    HttpStatusCode.TooManyRequests => PluggyErrorCodes.RateLimited,
                    _ => CodeOfUnexpected(response.StatusCode),
                },
                kind,
                status,
                pluggyCode);
        }
    }

    /// <summary>
    /// A status no rule above explains. Pluggy "did not answer" only when it really did not: its own failures (5xx)
    /// and a request it gave up waiting for (408). Anything else (410, 409, 422, 405, a redirect, a 400 that is not
    /// about an item...) is Pluggy refusing what was asked: repeating the same request gets the same answer.
    /// </summary>
    private static string CodeOfUnexpected(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout ? PluggyErrorCodes.Unavailable : PluggyErrorCodes.Refused;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string kind, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var watch = Stopwatch.StartNew();
        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Pluggy {Kind} call failed before an answer after {ElapsedMs} ms: {Error}.", kind, watch.ElapsedMilliseconds, ex.HttpRequestError);
            throw new PluggyException(PluggyErrorCodes.Unavailable, kind);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not the caller giving up: the client's own timeout.
            _logger.LogWarning("Pluggy {Kind} call timed out after {ElapsedMs} ms.", kind, watch.ElapsedMilliseconds);
            throw new PluggyException(PluggyErrorCodes.Unavailable, kind);
        }
    }

    // ------------------------------------------------------------------ the log of the calls

    /// <summary>
    /// One line per call that Pluggy answered well. Only numbers and fixed words: the kind of the call, the status,
    /// the time, the page asked, how many entries the listing brought and whether there is a next page.
    /// </summary>
    private void LogAnswer(string kind, int status, Stopwatch watch, int? page, JsonElement root)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;

        int? items = null;
        var hasNext = false;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                items = results.GetArrayLength();
            hasNext = root.TryGetProperty("next", out var next)
                      && next.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                      && !(next.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(next.GetString()));
        }

        _logger.LogInformation(
            "Pluggy {Kind} call answered {StatusCode} in {ElapsedMs} ms; page {Page}; items {Items}; next cursor {NextCursor}.",
            kind,
            status,
            watch.ElapsedMilliseconds,
            NumberOrDash(page),
            NumberOrDash(items),
            hasNext ? "present" : "absent");
    }

    private void LogRefusal(string kind, int status, Stopwatch watch, int? page, string? pluggyCode)
        => _logger.LogWarning(
            "Pluggy {Kind} call answered {StatusCode} in {ElapsedMs} ms; page {Page}; Pluggy error {PluggyCode}.",
            kind,
            status,
            watch.ElapsedMilliseconds,
            NumberOrDash(page),
            pluggyCode ?? "-");

    private static string NumberOrDash(int? number) => number is { } value ? value.ToString(CultureInfo.InvariantCulture) : "-";

    /// <summary>An error body of an API is a few hundred bytes: a larger one is not looked at.</summary>
    private const int MaxErrorBodyLength = 4096;

    /// <summary>Each of the two codes read from the error body is cut to this.</summary>
    private const int MaxErrorCodeLength = 64;

    /// <summary>
    /// <c>code</c> (a number or a text) and <c>codeDescription</c> of Pluggy's error body
    /// (<c>{"code":410,"codeDescription":"ENDPOINT_DEPRECATED","message":...}</c>), as "410/ENDPOINT_DEPRECATED". Its
    /// <c>message</c> is never read: it is a sentence, and a sentence may carry what was asked. Null when the body has
    /// neither, or is not what an error of Pluggy looks like. Never fails the call it is read for.
    /// </summary>
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentLength > MaxErrorBodyLength) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length is 0 or > MaxErrorBodyLength) return null;

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var code = root.TryGetProperty("code", out var c) ? CleanCode(c) : null;
            var description = root.TryGetProperty("codeDescription", out var d) ? CleanCode(d) : null;
            if (code is null) return description;
            return description is null ? code : $"{code}/{description}";
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Only ASCII letters, digits, "_", "-" and ".", and only the first <see cref="MaxErrorCodeLength"/> of them.</summary>
    private static string? CleanCode(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        if (text is null) return null;

        var clean = new StringBuilder(Math.Min(text.Length, MaxErrorCodeLength));
        foreach (var ch in text)
        {
            if (clean.Length == MaxErrorCodeLength) break;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.') clean.Append(ch);
        }

        return clean.Length == 0 ? null : clean.ToString();
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
        return new PluggyException(PluggyErrorCodes.Unavailable, kind);
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
