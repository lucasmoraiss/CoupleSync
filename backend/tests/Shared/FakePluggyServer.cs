using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

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
    public const string ItemWaitingUserInput = "a1b2c3d4-0000-4000-8000-000000000005";
    public const string UnknownItem = "a1b2c3d4-0000-4000-8000-00000000dead";

    public const string CheckingAccountId = "b1b2c3d4-0000-4000-8000-00000000000a";
    public const string CreditCardAccountId = "b1b2c3d4-0000-4000-8000-00000000000b";
    public const string OtherCheckingAccountId = "b1b2c3d4-0000-4000-8000-00000000000c";

    /// <summary>The full account number of the fixture: must never be stored nor returned whole.</summary>
    public const string CheckingAccountNumber = "0001/98765-1234";
    public const string CreditCardNumber = "xxxx xxxx xxxx 5678";

    // Transactions of the fixture (ids in the shape of the Pluggy documentation; names and values invented).
    public const string RestaurantTransactionId = "c1b2c3d4-0000-4000-8000-000000000101";
    public const string RideTransactionId = "c1b2c3d4-0000-4000-8000-000000000102";
    public const string SalaryTransactionId = "c1b2c3d4-0000-4000-8000-000000000103";
    public const string CardPurchaseTransactionId = "c1b2c3d4-0000-4000-8000-000000000201";
    public const string CardPendingTransactionId = "c1b2c3d4-0000-4000-8000-000000000202";
    public const string OtherAccountTransactionId = "c1b2c3d4-0000-4000-8000-000000000301";

    /// <summary>The CNPJ of the merchant of the fixture: all nines, with check digits that do not match (nobody has it).</summary>
    public const string MerchantCnpj = "99999999999999";

    /// <summary>A text that only exists inside the raw JSON of the fixture: must never reach a log nor an API answer.</summary>
    public const string RawOnlyMarker = "PROVIDER-CODE-ONLY-IN-RAW-JSON";

    public FakePluggyServer()
    {
        var today = DateTime.UtcNow.Date;
        Transactions[CheckingAccountId] =
        [
            new FakeTransaction(RestaurantTransactionId, today.AddDays(-2).AddHours(15), -58.90m)
            {
                Description = "Cantina Exemplo",
                DescriptionRaw = "COMPRA DEBITO CANTINA EXEMPLO",
                Category = "Eating out",
                CategoryId = "11010000",
                MerchantName = "Cantina Exemplo Ltda",
                MerchantCnpj = MerchantCnpj,
                Balance = 1175.66m,
            },
            new FakeTransaction(RideTransactionId, today.AddDays(-3), -23.40m)
            {
                Description = "Corrida Exemplo",
                Category = "Taxi and ride-hailing",
                CategoryId = "19010000",
                PaymentMethod = "PIX",
            },
            new FakeTransaction(SalaryTransactionId, today.AddDays(-5).AddHours(12), 3500.00m)
            {
                Type = "CREDIT",
                Description = "Pagamento Empresa Exemplo",
                Category = "Salary",
                CategoryId = "01010000",
            },
        ];
        Transactions[CreditCardAccountId] =
        [
            new FakeTransaction(CardPurchaseTransactionId, today.AddDays(-4).AddHours(18), 300.00m)
            {
                Description = "Loja Exemplo 2/6",
                Category = "Online shopping",
                CategoryId = "08010000",
                InstallmentNumber = 2,
                TotalInstallments = 6,
                BillId = "d1b2c3d4-0000-4000-8000-000000000001",
            },
            new FakeTransaction(CardPendingTransactionId, today.AddDays(-1).AddHours(20), 45.00m)
            {
                Description = "Farmacia Exemplo",
                Category = "Pharmacy",
                CategoryId = "18020000",
                Status = "PENDING",
            },
        ];
        Transactions[OtherCheckingAccountId] =
        [
            new FakeTransaction(OtherAccountTransactionId, today.AddDays(-2).AddHours(10), -12.00m)
            {
                Description = "Padaria Modelo",
                Category = "Groceries",
                CategoryId = "10000000",
            },
        ];
    }

    /// <summary>GET /transactions answers from here, by account id (replace or change to test other shapes).</summary>
    public Dictionary<string, List<FakeTransaction>> Transactions { get; } = new(StringComparer.Ordinal);

    /// <summary>The server never gives more than this per page, whatever the page size asked (to test the paging).</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>When set, the answer of GET /transactions has no "totalPages" (the client must notice the last page itself).</summary>
    public bool OmitTotalPages { get; set; }

    /// <summary>When set, GET /transactions answers this status.</summary>
    public HttpStatusCode? TransactionsStatus { get; set; }

    /// <summary>When set, PATCH /items/{id} answers this status.</summary>
    public HttpStatusCode? ItemUpdateStatus { get; set; }

    /// <summary>Status GET /items/{id} gives for an item, instead of the one of the fixture.</summary>
    public Dictionary<string, string> ItemStatusOverride { get; } = new(StringComparer.Ordinal);

    /// <summary>The (from, to) of every GET /transactions of an account, in order.</summary>
    public List<(string From, string To)> WindowsAsked(string accountId)
    {
        lock (_gate)
        {
            return Requests
                .Where(r => r.Method == "GET" && r.Path == "/transactions")
                .Select(r => HttpUtility.ParseQueryString(r.Query))
                .Where(q => q["accountId"] == accountId && (q["page"] ?? "1") == "1")
                .Select(q => (q["from"] ?? string.Empty, q["to"] ?? string.Empty))
                .ToList();
        }
    }

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

        if (request.Method == HttpMethod.Patch && path.StartsWith("/items/", StringComparison.Ordinal))
        {
            if (ItemUpdateStatus is { } updateStatus) return Json(updateStatus, """{"code":0,"message":"forced"}""");
            var updating = path["/items/".Length..];
            return updating is ItemWithAccounts or OtherItemWithAccounts or EmptyItem or ItemWithLoginError or ItemWaitingUserInput
                ? Json(HttpStatusCode.OK, ItemJson(updating, "Banco Exemplo", "UPDATING", "CREATED", null))
                : Json(HttpStatusCode.NotFound, """{"code":404,"message":"item not found"}""");
        }

        if (request.Method == HttpMethod.Get && path == "/transactions")
        {
            if (TransactionsStatus is { } transactionsStatus) return Json(transactionsStatus, """{"code":0,"message":"forced"}""");
            return Json(HttpStatusCode.OK, TransactionsPage(HttpUtility.ParseQueryString(request.RequestUri.Query)));
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/items/", StringComparison.Ordinal))
        {
            var itemId = path["/items/".Length..];
            if (ItemStatusOverride.TryGetValue(itemId, out var overridden))
                return Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", overridden, overridden, null));
            return itemId switch
            {
                ItemWithAccounts => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "UPDATED", "SUCCESS", null)),
                OtherItemWithAccounts => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Modelo", "UPDATED", "SUCCESS", null)),
                EmptyItem => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "UPDATED", "SUCCESS", null)),
                ItemWithLoginError => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "LOGIN_ERROR", "INVALID_CREDENTIALS", "Invalid credentials")),
                ItemWaitingUserInput => Json(HttpStatusCode.OK, ItemJson(itemId, "Banco Exemplo", "WAITING_USER_INPUT", "WAITING_USER_INPUT", null)),
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

    /// <summary>What Pluggy does with ?accountId=&amp;from=&amp;to=&amp;page=&amp;pageSize=: the days are inclusive, in UTC.</summary>
    private string TransactionsPage(System.Collections.Specialized.NameValueCollection query)
    {
        List<FakeTransaction> all;
        lock (_gate)
        {
            all = Transactions.TryGetValue(query["accountId"] ?? string.Empty, out var stored) ? stored.ToList() : [];
        }

        if (DateTime.TryParseExact(query["from"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from))
            all = all.Where(t => t.Date.Date >= from.Date).ToList();
        if (DateTime.TryParseExact(query["to"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var to))
            all = all.Where(t => t.Date.Date <= to.Date).ToList();

        var pageSize = Math.Min(int.TryParse(query["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out var asked) ? asked : 500, MaxPageSize);
        var page = int.TryParse(query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 1;
        var totalPages = (int)Math.Ceiling(all.Count / (double)pageSize);
        var results = string.Join(",", all.Skip((page - 1) * pageSize).Take(pageSize).Select(t => t.ToJson(query["accountId"]!)));
        var paging = OmitTotalPages ? string.Empty : $"\"totalPages\": {totalPages.ToString(CultureInfo.InvariantCulture)}, ";
        return $"{{ \"total\": {all.Count.ToString(CultureInfo.InvariantCulture)}, {paging}\"page\": {page.ToString(CultureInfo.InvariantCulture)}, \"results\": [{results}] }}";
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

/// <summary>
/// One transaction as GET /transactions lists it (the fields of the Pluggy documentation). A negative amount is money
/// leaving a bank account; on a credit card a purchase is positive, with type DEBIT.
/// </summary>
internal sealed record FakeTransaction(string Id, DateTime Date, decimal Amount)
{
    public string? Type { get; init; } = "DEBIT";
    public string? Description { get; init; }
    public string? DescriptionRaw { get; init; }
    public string? Category { get; init; }
    public string? CategoryId { get; init; }
    public string? Status { get; init; } = "POSTED";
    public string? CurrencyCode { get; init; } = "BRL";
    public string? MerchantName { get; init; }
    public string? MerchantCnpj { get; init; }
    public string? PaymentMethod { get; init; }
    public int? InstallmentNumber { get; init; }
    public int? TotalInstallments { get; init; }
    public string? BillId { get; init; }
    public decimal? Balance { get; init; }

    /// <summary>The date exactly as it goes in the JSON, when a test needs a shape other than the ISO instant.</summary>
    public string? DateText { get; init; }

    public string ToJson(string accountId)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["description"] = Description,
            ["descriptionRaw"] = DescriptionRaw,
            ["currencyCode"] = CurrencyCode,
            ["amount"] = Amount,
            ["date"] = DateText ?? Date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["balance"] = Balance,
            ["category"] = Category,
            ["categoryId"] = CategoryId,
            ["accountId"] = accountId,
            ["providerCode"] = FakePluggyServer.RawOnlyMarker,
            ["status"] = Status,
            ["type"] = Type,
            ["paymentData"] = PaymentMethod is null ? null : new Dictionary<string, object?> { ["paymentMethod"] = PaymentMethod },
            ["merchant"] = MerchantName is null
                ? null
                : new Dictionary<string, object?> { ["name"] = MerchantName, ["businessName"] = MerchantName, ["cnpj"] = MerchantCnpj, ["category"] = Category },
            ["creditCardMetadata"] = InstallmentNumber is null && BillId is null
                ? null
                : new Dictionary<string, object?> { ["installmentNumber"] = InstallmentNumber, ["totalInstallments"] = TotalInstallments, ["billId"] = BillId },
        };
        return JsonSerializer.Serialize(fields);
    }
}
