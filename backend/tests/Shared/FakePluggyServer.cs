using System.Net;
using System.Text;
using System.Text.Json;

namespace CoupleSync.TestSupport;

/// <summary>
/// The Pluggy API as far as the tests need it, in memory: no test ever talks to api.pluggy.ai. Every identifier,
/// name and number here is invented, in the shape of the examples of the Pluggy documentation.
/// </summary>
internal sealed class FakePluggyServer : HttpMessageHandler
{
    public const string BaseUrl = "https://pluggy.test";

    // Obviously fake credentials (never those of a real application).
    public const string ClientId = "00000000-fake-4000-8000-clientid0a1b";
    public const string ClientSecret = "fake-client-secret-not-real-9f8e7d6c";

    public const string ItemWithAccounts = "a1b2c3d4-0000-4000-8000-000000000001";
    public const string EmptyItem = "a1b2c3d4-0000-4000-8000-000000000002";
    public const string ItemWithLoginError = "a1b2c3d4-0000-4000-8000-000000000003";
    public const string OtherItemWithAccounts = "a1b2c3d4-0000-4000-8000-000000000004";
    public const string UnknownItem = "a1b2c3d4-0000-4000-8000-00000000dead";

    public const string CheckingAccountId = "b1b2c3d4-0000-4000-8000-00000000000a";
    public const string CreditCardAccountId = "b1b2c3d4-0000-4000-8000-00000000000b";
    public const string OtherCheckingAccountId = "b1b2c3d4-0000-4000-8000-00000000000c";

    /// <summary>The full account number of the fixture: must never be stored nor returned whole.</summary>
    public const string CheckingAccountNumber = "0001/98765-1234";
    public const string CreditCardNumber = "xxxx xxxx xxxx 5678";

    private readonly object _gate = new();
    private int _issuedKeys;
    private readonly HashSet<string> _validKeys = new(StringComparer.Ordinal);

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>When set, POST /auth answers this status whatever the credentials.</summary>
    public HttpStatusCode? AuthStatus { get; set; }

    /// <summary>When set, every data route (items, accounts) answers this status.</summary>
    public HttpStatusCode? DataStatus { get; set; }

    /// <summary>Every request fails before any answer (DNS, connection refused).</summary>
    public bool NetworkDown { get; set; }

    /// <summary>Every request times out (what HttpClient raises when its Timeout elapses).</summary>
    public bool TimesOut { get; set; }

    /// <summary>Body of GET /accounts for <see cref="ItemWithAccounts"/> (replace to test other shapes).</summary>
    public string AccountsJson { get; set; } = DefaultAccountsJson;

    /// <summary>
    /// Runs after a request is recorded and before it is answered: a test holds an answer here while something else
    /// happens (Pluggy taking its time).
    /// </summary>
    public Func<RecordedRequest, Task>? BeforeAnswer { get; set; }

    public int AuthCalls => Count("POST", "/auth");

    public int Count(string method, string path)
    {
        lock (_gate) return Requests.Count(r => r.Method == method && r.Path == path);
    }

    /// <summary>The API keys issued so far stop being accepted (what happens when a key expires at Pluggy).</summary>
    public void ExpireIssuedKeys()
    {
        lock (_gate) _validKeys.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        var apiKey = request.Headers.TryGetValues("X-API-KEY", out var values) ? values.FirstOrDefault() : null;
        var recorded = new RecordedRequest(request.Method.Method, path, request.RequestUri.Query, apiKey, body);
        lock (_gate) Requests.Add(recorded);

        if (!string.Equals(request.RequestUri.GetLeftPart(UriPartial.Authority), BaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The fake Pluggy server was called with another host: {request.RequestUri}.");

        if (BeforeAnswer is { } beforeAnswer) await beforeAnswer(recorded);

        if (NetworkDown) throw new HttpRequestException("Connection refused (fake).");
        if (TimesOut) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout (fake).", new TimeoutException());

        if (request.Method == HttpMethod.Post && path == "/auth")
        {
            if (AuthStatus is { } forced) return Json(forced, """{"code":0,"message":"forced"}""");
            using var json = JsonDocument.Parse(body ?? "{}");
            var clientId = json.RootElement.TryGetProperty("clientId", out var id) ? id.GetString() : null;
            var clientSecret = json.RootElement.TryGetProperty("clientSecret", out var secret) ? secret.GetString() : null;
            if (clientId != ClientId || clientSecret != ClientSecret)
                return Json(HttpStatusCode.Unauthorized, """{"code":401,"message":"Invalid credentials"}""");

            string key;
            lock (_gate)
            {
                key = $"fake-api-key-{++_issuedKeys}";
                _validKeys.Add(key);
            }

            return Json(HttpStatusCode.OK, "{\"apiKey\":\"" + key + "\"}");
        }

        bool authorized;
        lock (_gate) authorized = apiKey is not null && _validKeys.Contains(apiKey);
        if (!authorized) return Json(HttpStatusCode.Unauthorized, """{"code":401,"message":"Missing or invalid authorization token"}""");
        if (DataStatus is { } dataStatus) return Json(dataStatus, """{"code":0,"message":"forced"}""");

        if (request.Method == HttpMethod.Get && path.StartsWith("/items/", StringComparison.Ordinal))
        {
            var itemId = path["/items/".Length..];
            return itemId switch
            {
                ItemWithAccounts => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "UPDATED", "SUCCESS", null)),
                OtherItemWithAccounts => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Modelo", "UPDATED", "SUCCESS", null)),
                EmptyItem => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "UPDATED", "SUCCESS", null)),
                ItemWithLoginError => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "LOGIN_ERROR", "INVALID_CREDENTIALS", "Invalid credentials")),
                _ => Json(HttpStatusCode.NotFound, """{"code":404,"message":"item not found"}"""),
            };
        }

        if (request.Method == HttpMethod.Get && path == "/accounts")
        {
            var itemId = request.RequestUri.Query.Replace("?itemId=", "", StringComparison.Ordinal);
            return itemId switch
            {
                ItemWithAccounts => Json(HttpStatusCode.OK, AccountsJson),
                OtherItemWithAccounts => Json(HttpStatusCode.OK, OtherAccountsJson),
                _ => Json(HttpStatusCode.OK, """{"total":0,"totalPages":0,"page":1,"results":[]}"""),
            };
        }

        return Json(HttpStatusCode.NotFound, """{"code":404,"message":"route not found"}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ItemJson(string id, string connector, string status, string executionStatus, string? error)
    {
        var errorJson = error is null ? "null" : "{ \"code\": \"INVALID_CREDENTIALS\", \"message\": \"" + error + "\" }";
        return $$"""
        {
          "id": "{{id}}",
          "connector": { "id": 200, "name": "{{connector}}", "type": "PERSONAL_BANK", "country": "BR" },
          "status": "{{status}}",
          "executionStatus": "{{executionStatus}}",
          "createdAt": "2026-09-01T12:00:00.000Z",
          "updatedAt": "2026-10-06T09:30:15.123Z",
          "lastUpdatedAt": "2026-10-06T09:30:15.123Z",
          "error": {{errorJson}}
        }
        """;
    }

    public const string DefaultAccountsJson = $$"""
        {
          "total": 2,
          "totalPages": 1,
          "page": 1,
          "results": [
            {
              "id": "{{CheckingAccountId}}",
              "type": "BANK",
              "subtype": "CHECKING_ACCOUNT",
              "name": "Conta Corrente",
              "marketingName": "Conta Exemplo Plus",
              "number": "{{CheckingAccountNumber}}",
              "balance": 1234.56,
              "currencyCode": "BRL",
              "itemId": "{{ItemWithAccounts}}",
              "bankData": { "transferNumber": "000/0001/98765-1", "closingBalance": 1234.56 },
              "creditData": null
            },
            {
              "id": "{{CreditCardAccountId}}",
              "type": "CREDIT",
              "subtype": "CREDIT_CARD",
              "name": "Cartão Exemplo Platinum",
              "marketingName": "EXEMPLO PLATINUM",
              "number": "{{CreditCardNumber}}",
              "balance": 987.65,
              "currencyCode": "BRL",
              "itemId": "{{ItemWithAccounts}}",
              "bankData": null,
              "creditData": {
                "level": "PLATINUM",
                "brand": "MASTERCARD",
                "balanceCloseDate": "2026-10-20T00:00:00.000Z",
                "balanceDueDate": "2026-10-27T00:00:00.000Z",
                "availableCreditLimit": 4012.35,
                "balanceForeignCurrency": 0,
                "minimumPayment": 148.15,
                "creditLimit": 5000
              }
            }
          ]
        }
        """;

    private const string OtherAccountsJson = $$"""
        {
          "total": 1,
          "totalPages": 1,
          "page": 1,
          "results": [
            {
              "id": "{{OtherCheckingAccountId}}",
              "type": "BANK",
              "subtype": "SAVINGS_ACCOUNT",
              "name": "Poupança",
              "marketingName": null,
              "number": "4321",
              "balance": 10,
              "currencyCode": "BRL",
              "creditData": null
            }
          ]
        }
        """;
}

internal sealed record RecordedRequest(string Method, string Path, string Query, string? ApiKey, string? Body);
