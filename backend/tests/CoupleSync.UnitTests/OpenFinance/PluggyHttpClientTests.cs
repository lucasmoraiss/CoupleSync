using System.Globalization;
using System.Net;
using System.Web;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using CoupleSync.TestSupport;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.OpenFinance;

/// <summary>
/// Issue #24 — the Pluggy client against <see cref="FakePluggyServer"/> (an HttpMessageHandler: no network).
/// Fixtures in the shape of the Pluggy documentation, all invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class PluggyHttpClientTests
{
    private readonly FakePluggyServer _pluggy = new();
    private readonly AdjustableClock _clock = new();
    private readonly MemoryCache _cache;

    public PluggyHttpClientTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
    }

    private readonly ListLogger _log = new();

    private PluggyHttpClient Client(string baseUrl = FakePluggyServer.BaseUrl) => new(
        new SingleHandlerFactory(_pluggy),
        _cache,
        Options.Create(new OpenFinanceOptions { PluggyBaseUrl = baseUrl }),
        _log);

    private PluggyHttpClient ClientOf(HttpMessageHandler handler) => new(
        new SingleHandlerFactory(handler),
        _cache,
        Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }),
        _log);

    private const string StoredSecret = "fake-encrypted-secret-as-stored-1";

    private static PluggyAuth Connection(Guid? id = null, string storedSecret = StoredSecret)
        => PluggyAuth.ForConnection(id ?? Guid.NewGuid(), FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, storedSecret);

    private static async Task<string> CodeOfAsync(Func<Task> call)
        => (await Assert.ThrowsAsync<PluggyException>(call)).Code;

    // ---------------------------------------------------------------- auth

    [Fact]
    public async Task Authenticate_PostsTheCredentialsToAuth()
    {
        await Client().AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, CancellationToken.None);

        var request = Assert.Single(_pluggy.Requests);
        Assert.Equal(("POST", "/auth"), (request.Method, request.Path));
        Assert.Null(request.ApiKey);
        Assert.Equal(
            $$"""{"clientId":"{{FakePluggyServer.ClientId}}","clientSecret":"{{FakePluggyServer.ClientSecret}}"}""",
            request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PluggyErrorCodes.InvalidCredentials)]
    [InlineData(HttpStatusCode.Forbidden, PluggyErrorCodes.InvalidCredentials)]
    [InlineData(HttpStatusCode.BadRequest, PluggyErrorCodes.InvalidCredentials)]
    [InlineData(HttpStatusCode.TooManyRequests, PluggyErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, PluggyErrorCodes.Unavailable)]
    public async Task Authenticate_TranslatesTheStatusIntoAClosedCode(HttpStatusCode status, string expected)
    {
        _pluggy.AuthStatus = status;

        var code = await CodeOfAsync(() => Client().AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, CancellationToken.None));

        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task Authenticate_WithTheWrongSecret_IsInvalidCredentials()
    {
        var code = await CodeOfAsync(() => Client().AuthenticateAsync(FakePluggyServer.ClientId, "fake-wrong-secret", CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.InvalidCredentials, code);
    }

    [Fact]
    public async Task ANetworkFailure_OrATimeout_IsUnavailable_ButTheCallerGivingUpIsNot()
    {
        _pluggy.NetworkDown = true;
        Assert.Equal(PluggyErrorCodes.Unavailable,
            await CodeOfAsync(() => Client().AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, CancellationToken.None)));

        _pluggy.NetworkDown = false;
        _pluggy.TimesOut = true;
        Assert.Equal(PluggyErrorCodes.Unavailable,
            await CodeOfAsync(() => Client().AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, CancellationToken.None)));

        // The request itself was cancelled (the app closed the connection): that is not Pluggy being down.
        _pluggy.TimesOut = false;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, cancelled.Token));
    }

    [Theory]
    [InlineData("""{"apiKey":""}""")]
    [InlineData("""{"other":"x"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("<html>gateway</html>")]
    [InlineData("")]
    public async Task Authenticate_A200WithoutAUsableKey_IsUnavailable(string body)
    {
        var client = new PluggyHttpClient(
            new SingleHandlerFactory(new FixedAnswer(HttpStatusCode.OK, body)),
            _cache,
            Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }),
            NullLogger<PluggyHttpClient>.Instance);

        var code = await CodeOfAsync(() => client.AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.Unavailable, code);
    }

    // ---------------------------------------------------------------- item and accounts

    [Fact]
    public async Task GetItem_SendsTheApiKeyHeader_AndReadsTheItem()
    {
        var item = await Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(FakePluggyServer.ItemWithAccounts, item.Id);
        Assert.Equal("Banco Exemplo", item.ConnectorName);
        Assert.Equal("UPDATED", item.Status);
        Assert.Equal("SUCCESS", item.ExecutionStatus);
        Assert.Equal(new DateTime(2026, 10, 6, 9, 30, 15, 123, DateTimeKind.Utc), item.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, item.UpdatedAtUtc!.Value.Kind);
        Assert.Null(item.ErrorMessage);

        var request = _pluggy.Requests.Single(r => r.Method == "GET");
        Assert.Equal($"/items/{FakePluggyServer.ItemWithAccounts}", request.Path);
        Assert.Equal("fake-api-key-1", request.ApiKey);
    }

    [Fact]
    public async Task GetItem_WithAnError_CarriesTheStatusAndTheMessage()
    {
        var item = await Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithLoginError, CancellationToken.None);

        Assert.Equal(PluggyItemStatus.LoginError, item.Status);
        Assert.Equal("Invalid credentials", item.ErrorMessage);
    }

    [Fact]
    public async Task GetItem_ThatPluggyDoesNotKnow_IsItemNotFound()
    {
        var code = await CodeOfAsync(() => Client().GetItemAsync(Connection(), FakePluggyServer.UnknownItem, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.ItemNotFound, code);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, PluggyErrorCodes.ItemNotFound)]
    [InlineData(HttpStatusCode.Forbidden, PluggyErrorCodes.InvalidCredentials)]
    [InlineData(HttpStatusCode.TooManyRequests, PluggyErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PluggyErrorCodes.Unavailable)]
    public async Task DataCalls_TranslateTheStatusIntoAClosedCode(HttpStatusCode status, string expected)
    {
        _pluggy.DataStatus = status;

        Assert.Equal(expected, await CodeOfAsync(() => Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None)));
        Assert.Equal(expected, await CodeOfAsync(() => Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None)));
    }

    [Fact]
    public async Task TheItemId_IsEscapedIntoTheAddress()
    {
        // The API validates the id before it gets here; the client still never lets it change the route.
        await CodeOfAsync(() => Client().GetItemAsync(Connection(), "../auth?x=1", CancellationToken.None));

        var request = _pluggy.Requests.Single(r => r.Method == "GET");
        Assert.StartsWith("/items/", request.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth", request.Path, StringComparison.Ordinal);
        Assert.Equal(string.Empty, request.Query);
    }

    [Fact]
    public async Task GetAccounts_ReadsBankAndCreditAccounts()
    {
        var accounts = await Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(2, accounts.Count);
        var checking = accounts[0];
        Assert.Equal(FakePluggyServer.CheckingAccountId, checking.Id);
        Assert.Equal(("BANK", "CHECKING_ACCOUNT"), (checking.Type, checking.Subtype));
        Assert.Equal("Conta Corrente", checking.Name);
        Assert.Equal("Conta Exemplo Plus", checking.MarketingName);
        Assert.Equal(FakePluggyServer.CheckingAccountNumber, checking.Number);
        Assert.Equal(1234.56m, checking.Balance);
        Assert.Equal("BRL", checking.CurrencyCode);
        Assert.Null(checking.CreditData);

        var card = accounts[1];
        Assert.Equal(("CREDIT", "CREDIT_CARD"), (card.Type, card.Subtype));
        Assert.Equal(987.65m, card.Balance);
        Assert.NotNull(card.CreditData);
        Assert.Equal("PLATINUM", card.CreditData!.Level);
        Assert.Equal("MASTERCARD", card.CreditData.Brand);
        Assert.Equal(new DateOnly(2026, 10, 20), card.CreditData.BalanceCloseDate);
        Assert.Equal(new DateOnly(2026, 10, 27), card.CreditData.BalanceDueDate);
        Assert.Equal(4012.35m, card.CreditData.AvailableCreditLimit);
        Assert.Equal(5000m, card.CreditData.CreditLimit);
        Assert.Equal(148.15m, card.CreditData.MinimumPayment);

        var request = _pluggy.Requests.Single(r => r.Method == "GET");
        Assert.Equal(("/accounts", $"?itemId={FakePluggyServer.ItemWithAccounts}"), (request.Path, request.Query));
    }

    [Fact]
    public async Task GetAccounts_OfAnItemWithoutAccounts_IsAnEmptyList()
    {
        var accounts = await Client().GetAccountsAsync(Connection(), FakePluggyServer.EmptyItem, CancellationToken.None);

        Assert.Empty(accounts);
    }

    [Theory]
    [InlineData("pt-BR")] // comma as the decimal separator, dd/MM/yyyy
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]      // invariant, as the production image once ran
    public async Task NumbersAndDates_AreReadTheSameInEveryCulture(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture);
        try
        {
            var accounts = await Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);
            var item = await Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

            Assert.Equal(1234.56m, accounts[0].Balance);
            Assert.Equal(4012.35m, accounts[1].CreditData!.AvailableCreditLimit);
            Assert.Equal(new DateOnly(2026, 10, 20), accounts[1].CreditData!.BalanceCloseDate);
            Assert.Equal(new DateTime(2026, 10, 6, 9, 30, 15, 123, DateTimeKind.Utc), item.UpdatedAtUtc);

            // A number that arrives as text is the case a culture can get wrong ("1234.56" is 123456 in pt-BR).
            _pluggy.AccountsJson = """{"results":[{"id":"b1b2c3d4-0000-4000-8000-0000000000f1","type":"BANK","name":"Conta","balance":"1234.56"}]}""";
            var asText = await Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);
            Assert.Equal(1234.56m, asText[0].Balance);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public async Task GetAccounts_ToleratesWhatPluggyMayLeaveOutOrSendDifferently()
    {
        _pluggy.AccountsJson = """
            {
              "results": [
                { "id": "b1b2c3d4-0000-4000-8000-0000000000f1", "type": "BANK", "name": "Conta", "balance": "1234.56" },
                { "id": "b1b2c3d4-0000-4000-8000-0000000000f2", "type": "CREDIT", "subtype": "CREDIT_CARD", "name": "Cartão",
                  "number": null, "balance": null, "currencyCode": null,
                  "creditData": { "brand": null, "balanceCloseDate": "2026-11-05", "balanceDueDate": "not a date", "creditLimit": 1e3 } },
                { "type": "BANK", "name": "Sem id" },
                "not an object"
              ]
            }
            """;

        var accounts = await Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(2, accounts.Count); // the entry without an id and the stray text are skipped
        Assert.Equal(1234.56m, accounts[0].Balance); // a number written as text, with a dot
        Assert.Null(accounts[0].Subtype);
        Assert.Null(accounts[0].CurrencyCode);
        Assert.Equal(0m, accounts[1].Balance);
        Assert.Null(accounts[1].Number);
        Assert.Equal(new DateOnly(2026, 11, 5), accounts[1].CreditData!.BalanceCloseDate);
        Assert.Null(accounts[1].CreditData!.BalanceDueDate);
        Assert.Equal(1000m, accounts[1].CreditData!.CreditLimit);
        Assert.Null(accounts[1].CreditData!.MinimumPayment);
    }

    [Theory]
    [InlineData("""{"total":0}""")]
    [InlineData("""{"results":"none"}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    public async Task GetAccounts_AnAnswerThatIsNotAListOfAccounts_IsUnavailable_NotAnEmptyItem(string body)
    {
        _pluggy.AccountsJson = body;

        var code = await CodeOfAsync(() => Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.Unavailable, code);
    }

    // ---------------------------------------------------------------- the API key of a connection

    [Fact]
    public async Task TheApiKeyOfAConnection_IsReusedBetweenCalls_AndBetweenClients()
    {
        var auth = Connection();

        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetAccountsAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetItemAsync(auth, FakePluggyServer.OtherItemWithAccounts, CancellationToken.None);

        Assert.Equal(1, _pluggy.AuthCalls);
        Assert.All(_pluggy.Requests.Where(r => r.Method == "GET"), r => Assert.Equal("fake-api-key-1", r.ApiKey));
    }

    [Fact]
    public async Task EachConnection_HasItsOwnApiKey()
    {
        await Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(2, _pluggy.AuthCalls);
        Assert.Equal(new[] { "fake-api-key-1", "fake-api-key-2" }, _pluggy.Requests.Where(r => r.Method == "GET").Select(r => r.ApiKey));
    }

    [Fact]
    public async Task TheApiKey_IsKeptFor110Minutes()
    {
        var auth = Connection();
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        _clock.UtcNow += TimeSpan.FromMinutes(109);
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        Assert.Equal(1, _pluggy.AuthCalls);

        // Past 110 minutes (Pluggy's own key lasts 120) the next call authenticates again.
        _clock.UtcNow += TimeSpan.FromMinutes(2);
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        Assert.Equal(2, _pluggy.AuthCalls);
    }

    [Fact]
    public void TheNamedHttpClient_HasA30SecondTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PluggyHttpClient.RequestTimeout);
        Assert.Equal("Pluggy", PluggyHttpClient.HttpClientName);
    }

    [Fact]
    public async Task A401OnADataCall_DropsTheKey_AuthenticatesAgain_AndRepeatsTheCallOnce()
    {
        var auth = Connection();
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        _pluggy.ExpireIssuedKeys(); // Pluggy no longer accepts fake-api-key-1

        var item = await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal("Banco Exemplo", item.ConnectorName);
        Assert.Equal(2, _pluggy.AuthCalls);
        Assert.Equal(
            new[] { "fake-api-key-1", "fake-api-key-1", "fake-api-key-2" },
            _pluggy.Requests.Where(r => r.Method == "GET").Select(r => r.ApiKey));

        // The new key is the one kept from now on.
        await Client().GetAccountsAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        Assert.Equal(2, _pluggy.AuthCalls);
    }

    [Fact]
    public async Task A401Twice_IsInvalidCredentials_AndTheCallIsNotRepeatedForever()
    {
        var always401 = new AlwaysUnauthorizedData();
        var client = new PluggyHttpClient(
            new SingleHandlerFactory(always401),
            _cache,
            Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }),
            NullLogger<PluggyHttpClient>.Instance);

        var code = await CodeOfAsync(() => client.GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.InvalidCredentials, code);
        Assert.Equal(2, always401.AuthCalls);
        Assert.Equal(2, always401.DataCalls);
    }

    [Fact]
    public async Task ForgetConnection_DropsTheKey_SoNewCredentialsNeverUseTheOldKey()
    {
        var connectionId = Guid.NewGuid();
        var client = Client();
        await client.GetItemAsync(Connection(connectionId), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        client.ForgetConnection(connectionId);
        await client.GetItemAsync(Connection(connectionId), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(2, _pluggy.AuthCalls);
        Assert.Equal("fake-api-key-2", _pluggy.Requests.Last().ApiKey);
    }

    [Fact]
    public async Task CredentialsWithoutAConnection_AreNotCached()
    {
        var auth = PluggyAuth.Of(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret);

        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(2, _pluggy.AuthCalls);
    }

    [Fact]
    public async Task TheBaseAddress_ComesFromTheOptions_WithOrWithoutATrailingSlash()
    {
        await Client(FakePluggyServer.BaseUrl + "/").GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(new[] { "/auth", $"/items/{FakePluggyServer.ItemWithAccounts}" }, _pluggy.Requests.Select(r => r.Path));
        Assert.Equal("https://api.pluggy.ai", new OpenFinanceOptions().PluggyBaseUrl);
    }

    [Fact]
    public void PluggyAuth_NeverPrintsTheCredentials()
    {
        var text = Connection().ToString() + PluggyAuth.Of(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret);

        Assert.DoesNotContain(FakePluggyServer.ClientSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePluggyServer.ClientId, text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- issue #25: the key belongs to the stored credentials

    [Fact]
    public async Task TheApiKey_IsNeverReusedForOtherStoredCredentialsOfTheSameConnection()
    {
        var connectionId = Guid.NewGuid();
        await Client().GetItemAsync(Connection(connectionId, "stored-secret-A"), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        // Disconnected and connected again (nobody called ForgetConnection): the stored secret is another text.
        await Client().GetItemAsync(Connection(connectionId, "stored-secret-B"), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(2, _pluggy.AuthCalls);
        Assert.Equal(new[] { "fake-api-key-1", "fake-api-key-2" }, _pluggy.Requests.Where(r => r.Method == "GET").Select(r => r.ApiKey));

        // The key of the new credentials is the one kept; the same credentials reuse it.
        await Client().GetItemAsync(Connection(connectionId, "stored-secret-B"), FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        Assert.Equal(2, _pluggy.AuthCalls);
        Assert.Equal("fake-api-key-2", _pluggy.Requests.Last().ApiKey);
    }

    [Fact]
    public async Task AKeyObtainedWithErasedCredentials_ThatArrivesLate_IsNotUsedByTheNewOnes()
    {
        var connectionId = Guid.NewGuid();
        // The new credentials got their key first...
        await Client().GetItemAsync(Connection(connectionId, "stored-secret-B"), FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        // ...and a request that still had the old ones in memory stores its key afterwards.
        await Client().GetItemAsync(Connection(connectionId, "stored-secret-A"), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        await Client().GetItemAsync(Connection(connectionId, "stored-secret-B"), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        Assert.Equal(3, _pluggy.AuthCalls);
        Assert.Equal(
            new[] { "fake-api-key-1", "fake-api-key-2", "fake-api-key-3" },
            _pluggy.Requests.Where(r => r.Method == "GET").Select(r => r.ApiKey));
    }

    [Fact]
    public void TheCache_KeepsNeitherTheStoredSecretNorPrintsTheKey()
    {
        var auth = Connection(storedSecret: "stored-secret-A");

        Assert.Equal("stored-secret-A", auth.CredentialsVersion);
        Assert.DoesNotContain("stored-secret-A", auth.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- issue #25: transactions

    private static readonly DateOnly From = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(-3));
    private static readonly DateOnly To = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public async Task GetTransactions_AsksForTheAccountAndTheDays_AndReadsEveryField()
    {
        var listing = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);
        var transactions = listing.Transactions;

        // Issue #77: the listing by cursor, with the names of its parameters and nothing it refuses.
        var request = _pluggy.Requests.Last();
        Assert.Equal(("GET", "/v2/transactions"), (request.Method, request.Path));
        Assert.Equal(
            $"?accountId={FakePluggyServer.CheckingAccountId}&dateFrom={From:yyyy-MM-dd}&dateTo={To:yyyy-MM-dd}",
            request.Query);
        Assert.Equal("fake-api-key-1", request.ApiKey);
        Assert.True(listing.Complete);

        Assert.Equal(3, transactions.Count);
        var restaurant = transactions.Single(t => t.Id == FakePluggyServer.RestaurantTransactionId);
        Assert.Equal(-58.90m, restaurant.Amount);
        Assert.Equal("DEBIT", restaurant.Type);
        Assert.Equal("BRL", restaurant.CurrencyCode);
        Assert.Equal("Cantina Exemplo", restaurant.Description);
        Assert.Equal("COMPRA DEBITO CANTINA EXEMPLO", restaurant.DescriptionRaw);
        Assert.Equal(("Eating out", "11010000"), (restaurant.Category, restaurant.CategoryId));
        Assert.Equal(("Cantina Exemplo Ltda", FakePluggyServer.MerchantCnpj), (restaurant.MerchantName, restaurant.MerchantCnpj));
        Assert.Equal("POSTED", restaurant.Status);
        Assert.Equal(1175.66m, restaurant.Balance);
        Assert.Equal(DateTimeKind.Utc, restaurant.DateUtc.Kind);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-2).AddHours(15), restaurant.DateUtc);
        // The element exactly as Pluggy sent it, with what the client does not read too.
        Assert.Contains(FakePluggyServer.RawOnlyMarker, restaurant.RawJson, StringComparison.Ordinal);
        Assert.StartsWith("{", restaurant.RawJson, StringComparison.Ordinal);

        Assert.Equal("PIX", transactions.Single(t => t.Id == FakePluggyServer.RideTransactionId).PaymentMethod);
        var salary = transactions.Single(t => t.Id == FakePluggyServer.SalaryTransactionId);
        Assert.Equal(("CREDIT", 3500.00m), (salary.Type, salary.Amount));
    }

    [Fact]
    public async Task GetTransactions_ReadsWhoPaid_WhenTheBankSaysSo_AndNeverPrintsTheName()
    {
        var today = DateTime.UtcNow.Date;
        _pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("t26-with-payer", today.AddDays(-1), 300.00m) { Type = "CREDIT", PayerName = "BRUNO EXEMPLO SILVA", PaymentMethod = "PIX" },
            new FakeTransaction("t26-method-only", today.AddDays(-1), 310.00m) { Type = "CREDIT", PaymentMethod = "PIX" },
            new FakeTransaction("t26-no-payment-data", today.AddDays(-1), 320.00m) { Type = "CREDIT" },
        ];

        var transactions = (await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None)).Transactions;

        var withPayer = transactions.Single(t => t.Id == "t26-with-payer");
        Assert.Equal("BRUNO EXEMPLO SILVA", withPayer.PayerName);
        Assert.Equal("PIX", withPayer.PaymentMethod);
        Assert.Null(transactions.Single(t => t.Id == "t26-method-only").PayerName);
        Assert.Null(transactions.Single(t => t.Id == "t26-no-payment-data").PayerName);
        Assert.DoesNotContain("BRUNO", withPayer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTransactions_OfACreditCard_ReadsInstallmentsBillAndPendingStatus()
    {
        var transactions = (await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CreditCardAccountId, From, To, CancellationToken.None)).Transactions;

        var purchase = transactions.Single(t => t.Id == FakePluggyServer.CardPurchaseTransactionId);
        Assert.Equal(300.00m, purchase.Amount);
        Assert.Equal((2, 6), (purchase.InstallmentNumber, purchase.TotalInstallments));
        Assert.Equal("d1b2c3d4-0000-4000-8000-000000000001", purchase.BillId);
        Assert.Equal("PENDING", transactions.Single(t => t.Id == FakePluggyServer.CardPendingTransactionId).Status);
    }

    // Issue #77: the listing is paged by cursor (GET /v2/transactions). This takes the place of the test of the pages
    // by number, with and without "totalPages": the cursor listing has neither a page number nor a page count.
    [Theory]
    [InlineData(nameof(FakeNextShape.QueryString))]
    [InlineData(nameof(FakeNextShape.QueryStringWithoutQuestionMark))]
    [InlineData(nameof(FakeNextShape.BareCursor))]
    [InlineData(nameof(FakeNextShape.AbsoluteUrl))]
    [InlineData(nameof(FakeNextShape.RelativeUrl))]
    public async Task GetTransactions_FollowsTheCursorUntilTheLastPage_WhateverTheFormOfNext(string shape)
    {
        var today = DateTime.UtcNow.Date;
        _pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 1203)
            .Select(i => new FakeTransaction($"c1b2c3d4-0000-4000-8000-{i:D12}", today.AddDays(-1).AddSeconds(i), -i))
            .ToList();
        _pluggy.NextShape = Enum.Parse<FakeNextShape>(shape);

        var listing = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        Assert.True(listing.Complete);
        Assert.Equal(1203, listing.Transactions.Count);
        Assert.Equal(1203, listing.Transactions.Select(t => t.Id).Distinct().Count());
        var pages = _pluggy.Requests.Where(r => r.Path == FakePluggyServer.TransactionsPath).Select(r => HttpUtility.ParseQueryString(r.Query)).ToList();
        // The cursor goes back as Pluggy gave it (decoded from "next", escaped again in the address).
        Assert.Equal(new[] { null, FakePluggyServer.CursorOf(500), FakePluggyServer.CursorOf(1000) }, pages.Select(q => q["after"]));
        // Every page asks for the same account and days, and nothing the cursor listing refuses (page, pageSize, from, to).
        Assert.All(pages, q =>
        {
            Assert.Equal(FakePluggyServer.CheckingAccountId, q["accountId"]);
            Assert.Equal(From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), q["dateFrom"]);
            Assert.Equal(To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), q["dateTo"]);
            Assert.Subset(new HashSet<string?> { "accountId", "dateFrom", "dateTo", "after" }, q.AllKeys.ToHashSet());
        });
        Assert.Equal(0, _pluggy.Count("GET", FakePluggyServer.LegacyTransactionsPath));
        Assert.Equal(1, _pluggy.AuthCalls);
    }

    [Fact]
    public async Task TheFixtureAnswersAsPluggyDoes_410OnTheOldRoute_And400ForAParameterTheCursorListingDoesNotKnow()
    {
        await Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None); // a key is issued
        using var http = new HttpClient(_pluggy, disposeHandler: false);
        http.DefaultRequestHeaders.Add(PluggyHttpClient.ApiKeyHeader, "fake-api-key-1");
        var account = FakePluggyServer.CheckingAccountId;

        var legacy = await http.GetAsync($"{FakePluggyServer.BaseUrl}/transactions?accountId={account}&from=2026-07-01&to=2026-10-07&pageSize=500&page=1");
        Assert.Equal(HttpStatusCode.Gone, legacy.StatusCode);
        Assert.Contains("ENDPOINT_DEPRECATED", await legacy.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        foreach (var refused in new[] { "page=1", "pageSize=500", "from=2026-07-01", "to=2026-10-07" })
        {
            var answer = await http.GetAsync($"{FakePluggyServer.BaseUrl}/v2/transactions?accountId={account}&{refused}");
            Assert.True(HttpStatusCode.BadRequest == answer.StatusCode, refused);
        }

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{FakePluggyServer.BaseUrl}/v2/transactions?accountId={account}&dateFrom=2026-07-01&dateTo=2026-10-07")).StatusCode);
    }

    [Fact]
    public async Task GetTransactions_WhenNextIsTheCursorJustSent_StopsThere_AndSaysTheListingIsNotComplete()
    {
        var today = DateTime.UtcNow.Date;
        _pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 5)
            .Select(i => new FakeTransaction($"c1b2c3d4-0000-4000-8000-{i:D12}", today.AddDays(-1).AddSeconds(i), -i))
            .ToList();
        _pluggy.MaxPageSize = 2;
        _pluggy.RepeatCursor = true;

        var listing = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        Assert.False(listing.Complete);
        Assert.Equal(4, listing.Transactions.Count); // the two pages read are kept
        Assert.Equal(2, _pluggy.Count("GET", FakePluggyServer.TransactionsPath)); // and the same page is not asked for again
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("repeated a cursor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTransactions_WhenAnOlderCursorComesBack_StopsThere_AndSaysTheListingIsNotComplete()
    {
        var handler = new PagesAnswer(
            Page("t-1", "\"?accountId=account&after=cursor-A\""),
            Page("t-2", "\"?accountId=account&after=cursor-B\""),
            Page("t-3", "\"?accountId=account&after=cursor-A\""),
            Page("t-4", "null"));

        var listing = await ClientOf(handler).GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None);

        Assert.False(listing.Complete);
        Assert.Equal(new[] { "t-1", "t-2", "t-3" }, listing.Transactions.Select(t => t.Id));
        Assert.Equal(3, handler.DataCalls);
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("repeated a cursor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("12345")]                                           // not a text
    [InlineData("{\"after\":\"cursor-A\"}")]                         // an object
    [InlineData("true")]
    [InlineData("\"https://pluggy.test/v2/transactions\"")]          // an address without the cursor
    [InlineData("\"?accountId=account\"")]                           // a query string without the cursor
    [InlineData("\"accountId=account&dateFrom=2026-07-01\"")]
    [InlineData("\"?accountId=account&after=\"")]                    // an empty cursor
    [InlineData(null)]                                              // no "next" at all
    public async Task GetTransactions_WhenNextCannotBeRead_StopsThere_AndSaysTheListingIsNotComplete(string? nextJson)
    {
        var handler = new PagesAnswer(Page("t-1", nextJson), Page("t-2", "null"));

        var listing = await ClientOf(handler).GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None);

        Assert.False(listing.Complete);
        Assert.Equal("t-1", Assert.Single(listing.Transactions).Id);
        Assert.Equal(1, handler.DataCalls);
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task GetTransactions_WhenNextIsNullOrEmpty_TheListingIsComplete(string nextJson)
    {
        var handler = new PagesAnswer(Page("t-1", nextJson), Page("t-2", "null"));

        var listing = await ClientOf(handler).GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None);

        Assert.True(listing.Complete);
        Assert.Equal(1, handler.DataCalls);
    }

    [Fact]
    public async Task GetTransactions_OfAListingThatNeverEnds_StopsAtTheCeilingOfPages_AndSaysItIsNotComplete()
    {
        _pluggy.NeverEnds = true;

        var listing = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        Assert.False(listing.Complete);
        Assert.Equal(3, listing.Transactions.Count);
        Assert.Equal(PluggyHttpClient.MaxTransactionPages, _pluggy.Count("GET", FakePluggyServer.TransactionsPath));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("ceiling", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("?accountId=account&after=a%2Bb%2Fc%3D%3D", "a+b/c==")]   // escaped, as an address carries it
    [InlineData("?after=a+b/c==&accountId=account", "a+b/c==")]          // not escaped: taken as written
    [InlineData("a+b/c==", "a+b/c==")]                                   // the bare cursor is never decoded
    [InlineData("a%2Bb", "a%2Bb")]
    [InlineData("  after=cursor-A  ", "cursor-A")]
    [InlineData("https://api.pluggy.ai/v2/transactions?accountId=account&after=cursor%20A#top", "cursor A")]
    public async Task GetTransactions_SendsBackOnlyTheCursorOfNext_Decoded(string next, string cursor)
    {
        var handler = new PagesAnswer(Page("t-1", System.Text.Json.JsonSerializer.Serialize(next)), Page("t-2", "null"));

        var listing = await ClientOf(handler).GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None);

        Assert.True(listing.Complete);
        Assert.Equal(2, listing.Transactions.Count);
        var second = handler.DataQueries[1];
        Assert.Equal(
            $"?accountId=account&dateFrom={From:yyyy-MM-dd}&dateTo={To:yyyy-MM-dd}&after={Uri.EscapeDataString(cursor)}",
            second);
    }

    [Fact]
    public async Task GetTransactions_OfAnAccountWithoutTransactions_IsAnEmptyList_InOneCall()
    {
        _pluggy.Transactions.Remove(FakePluggyServer.CheckingAccountId);

        var listing = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        Assert.Empty(listing.Transactions);
        Assert.True(listing.Complete);
        Assert.Equal(1, _pluggy.Count("GET", FakePluggyServer.TransactionsPath));
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public async Task GetTransactions_ReadsNumbersAndDatesTheSameInEveryCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            _pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
            [
                new FakeTransaction("c1b2c3d4-0000-4000-8000-00000000aaaa", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), -1234.56m)
                {
                    DateText = "2026-10-05T00:00:00.000Z",
                },
            ];

            var transaction = Assert.Single((await Client().GetTransactionsAsync(
                Connection(), FakePluggyServer.CheckingAccountId, new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 7), CancellationToken.None)).Transactions);

            Assert.Equal(-1234.56m, transaction.Amount);
            Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), transaction.DateUtc);
            Assert.EndsWith("&dateFrom=2026-07-01&dateTo=2026-10-07", _pluggy.Requests.Last().Query, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task GetTransactions_SkipsWhatHasNoIdDateOrValue_AndToleratesMissingParts()
    {
        var handler = new FixedDataAnswer("""
            { "results": [
              { "id": "t-ok", "date": "2026-10-05T12:00:00.000Z", "amount": -10 },
              { "date": "2026-10-05T12:00:00.000Z", "amount": -10 },
              { "id": "t-no-date", "amount": -10 },
              { "id": "t-no-amount", "date": "2026-10-05T12:00:00.000Z" },
              "not an object"
            ], "next": null }
            """);
        var client = new PluggyHttpClient(
            new SingleHandlerFactory(handler), _cache,
            Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }), NullLogger<PluggyHttpClient>.Instance);

        var transaction = Assert.Single((await client.GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None)).Transactions);

        Assert.Equal("t-ok", transaction.Id);
        Assert.Null(transaction.Type);
        Assert.Null(transaction.MerchantName);
        Assert.Null(transaction.InstallmentNumber);
        Assert.Null(transaction.Balance);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"message":"ok"}""")]
    [InlineData("""{"results":"none"}""")]
    public async Task GetTransactions_AnAnswerThatIsNotAListOfTransactions_IsUnavailable(string body)
    {
        var client = new PluggyHttpClient(
            new SingleHandlerFactory(new FixedDataAnswer(body)), _cache,
            Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }), NullLogger<PluggyHttpClient>.Instance);

        Assert.Equal(PluggyErrorCodes.Unavailable, await CodeOfAsync(() => client.GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None)));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, PluggyErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, PluggyErrorCodes.InvalidCredentials)]
    [InlineData(HttpStatusCode.NotFound, PluggyErrorCodes.ItemNotFound)]
    // Issue #77: "did not answer" is only for no answer at all and for 5xx; a refusal says it is a refusal.
    [InlineData(HttpStatusCode.BadGateway, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.Gone, PluggyErrorCodes.Refused)]
    [InlineData(HttpStatusCode.Conflict, PluggyErrorCodes.Refused)]
    [InlineData(HttpStatusCode.UnprocessableEntity, PluggyErrorCodes.Refused)]
    [InlineData(HttpStatusCode.MethodNotAllowed, PluggyErrorCodes.Refused)]
    [InlineData(HttpStatusCode.Found, PluggyErrorCodes.Refused)]
    [InlineData(HttpStatusCode.NotModified, PluggyErrorCodes.Refused)]
    // A 400 of the listing is about what was asked (a parameter, a cursor), never about the Item ID.
    [InlineData(HttpStatusCode.BadRequest, PluggyErrorCodes.Refused)]
    public async Task GetTransactions_TranslatesTheStatusIntoAClosedCode(HttpStatusCode status, string expected)
    {
        _pluggy.TransactionsStatus = status;

        Assert.Equal(expected, await CodeOfAsync(() => Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None)));
    }

    // ---------------------------------------------------------------- issue #77: honest errors

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Found)]
    public async Task AStatusNothingElseExplains_IsARefusal_OnEveryCall_NotPluggyNotAnswering(HttpStatusCode status)
    {
        _pluggy.DataStatus = status;
        Assert.Equal(PluggyErrorCodes.Refused, await CodeOfAsync(() => Client().GetItemAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None)));
        Assert.Equal(PluggyErrorCodes.Refused, await CodeOfAsync(() => Client().GetAccountsAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None)));

        _pluggy.DataStatus = null;
        _pluggy.ItemUpdateStatus = status;
        Assert.Equal(PluggyErrorCodes.Refused, await CodeOfAsync(() => Client().RequestItemUpdateAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None)));

        _pluggy.AuthStatus = status;
        Assert.Equal(PluggyErrorCodes.Refused, await CodeOfAsync(() => Client().AuthenticateAsync(FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, CancellationToken.None)));
    }

    [Fact]
    public void ARefusal_HasItsOwnCode_APortugueseMessageThatSaysRepeatingDoesNotHelp_And502()
    {
        var refusal = new PluggyException(PluggyErrorCodes.Refused);

        Assert.Equal("PLUGGY_REFUSED", refusal.Code);
        Assert.Equal(502, refusal.StatusCode);
        Assert.Equal(
            "O Pluggy recusou o pedido do CoupleSync. Tentar de novo não adianta: o problema precisa de uma correção no aplicativo.",
            refusal.Message);
        Assert.NotEqual(PluggyException.MessageFor(PluggyErrorCodes.Unavailable), refusal.Message);
    }

    [Fact]
    public async Task The410OfADiscontinuedRoute_IsARefusal_ThatCarriesTheCallTheStatusAndWhatPluggySaid()
    {
        _pluggy.TransactionsStatus = HttpStatusCode.Gone;
        _pluggy.TransactionsErrorBody = FakePluggyServer.LegacyTransactionsGoneBody; // "code" is a number here

        var failure = await Assert.ThrowsAsync<PluggyException>(
            () => Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.Refused, failure.Code);
        Assert.Equal("transactions", failure.CallKind);
        Assert.Equal(410, failure.HttpStatus);
        Assert.Equal("410/ENDPOINT_DEPRECATED", failure.PluggyCode);
        Assert.Contains(_log.Lines, l =>
            l.StartsWith("Warning", StringComparison.Ordinal)
            && l.Contains("Pluggy transactions call answered 410", StringComparison.Ordinal)
            && l.Contains("410/ENDPOINT_DEPRECATED", StringComparison.Ordinal));
        // What Pluggy wrote for a person to read is not copied anywhere.
        Assert.DoesNotContain(_log.Lines, l => l.Contains("This endpoint is deprecated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"code":"INVALID_CURSOR","message":"x"}""", "INVALID_CURSOR")]                      // "code" as text
    [InlineData("""{"code":400,"message":"x"}""", "400")]
    [InlineData("""{"codeDescription":"VALIDATION_ERROR"}""", "VALIDATION_ERROR")]
    [InlineData("""{"code":400.5,"codeDescription":"A B\r\nC <script> ção"}""", "400.5/ABCscripto")]   // only letters, digits, "_", "-" and "."
    [InlineData("""{"code":null,"codeDescription":{"x":1}}""", null)]
    [InlineData("""{"message":"only a message"}""", null)]
    [InlineData("""["not","an","object"]""", null)]
    [InlineData("<html>gateway</html>", null)]
    [InlineData("", null)]
    public async Task TheErrorBodyOfPluggy_IsReadForItsCodes_CleanedAndNeverTrusted(string body, string? expected)
    {
        _pluggy.TransactionsStatus = HttpStatusCode.BadRequest;
        _pluggy.TransactionsErrorBody = body;

        var failure = await Assert.ThrowsAsync<PluggyException>(
            () => Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.Refused, failure.Code);
        Assert.Equal(400, failure.HttpStatus);
        Assert.Equal(expected, failure.PluggyCode);
    }

    [Fact]
    public async Task TheErrorBodyOfPluggy_IsCutToALimit()
    {
        _pluggy.TransactionsStatus = HttpStatusCode.BadRequest;
        _pluggy.TransactionsErrorBody = "{\"code\":\"" + new string('A', 300) + "\",\"codeDescription\":\"" + new string('B', 300) + "\"}";
        var cut = await Assert.ThrowsAsync<PluggyException>(
            () => Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));
        Assert.Equal(new string('A', 64) + "/" + new string('B', 64), cut.PluggyCode);

        // A body far larger than an error of an API is not even looked at.
        _pluggy.TransactionsErrorBody = "{\"codeDescription\":\"TOO_BIG\",\"padding\":\"" + new string('x', 20_000) + "\"}";
        var ignored = await Assert.ThrowsAsync<PluggyException>(
            () => Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));
        Assert.Equal(PluggyErrorCodes.Refused, ignored.Code);
        Assert.Null(ignored.PluggyCode);
    }

    [Fact]
    public async Task AFailureWithoutAnAnswer_CarriesTheCall_AndNoStatus()
    {
        var auth = Connection();
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        _pluggy.NetworkDown = true;

        var failure = await Assert.ThrowsAsync<PluggyException>(
            () => Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));

        Assert.Equal(PluggyErrorCodes.Unavailable, failure.Code);
        Assert.Equal("transactions", failure.CallKind);
        Assert.Null(failure.HttpStatus);
        Assert.Null(failure.PluggyCode);
        // One line for this call too, with how long it took to fail.
        Assert.Single(_log.Lines, l => System.Text.RegularExpressions.Regex.IsMatch(
            l, @"^Warning: Pluggy transactions call failed before an answer after \d+ ms: .+\.$"));

        _pluggy.NetworkDown = false;
        _pluggy.TimesOut = true;
        await CodeOfAsync(() => Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));
        Assert.Single(_log.Lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^Warning: Pluggy transactions call timed out after \d+ ms\.$"));
    }

    // ---------------------------------------------------------------- issue #77: one line of log per call

    [Fact]
    public async Task EveryCallToPluggy_WritesOneInformationLine_WithTheKindTheStatusTheTimeThePageAndTheCount()
    {
        _pluggy.MaxPageSize = 2;
        var auth = Connection();

        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetAccountsAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);
        await Client().GetBillsAsync(auth, FakePluggyServer.CreditCardAccountId, CancellationToken.None);

        var lines = _log.Lines.Where(l => l.StartsWith("Information", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, lines.Count); // auth, item, accounts, two pages of transactions, bills
        Assert.Matches(@"^Information: Pluggy auth call answered 200 in \d+ ms; page -; items -; next cursor absent\.$", lines[0]);
        Assert.Matches(@"^Information: Pluggy item call answered 200 in \d+ ms; page -; items -; next cursor absent\.$", lines[1]);
        Assert.Matches(@"^Information: Pluggy accounts call answered 200 in \d+ ms; page -; items 2; next cursor absent\.$", lines[2]);
        Assert.Matches(@"^Information: Pluggy transactions call answered 200 in \d+ ms; page 1; items 2; next cursor present\.$", lines[3]);
        Assert.Matches(@"^Information: Pluggy transactions call answered 200 in \d+ ms; page 2; items 1; next cursor absent\.$", lines[4]);
        Assert.Matches(@"^Information: Pluggy bills call answered 200 in \d+ ms; page 1; items 0; next cursor absent\.$", lines[5]);
    }

    [Fact]
    public async Task TheLogOfTheCalls_HasNoSecret_NoIdentifierOfTheBank_NoCursor_AndNothingOfATransaction()
    {
        _pluggy.MaxPageSize = 1;
        var auth = Connection();
        await Client().GetItemAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetAccountsAsync(auth, FakePluggyServer.ItemWithAccounts, CancellationToken.None);
        await Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);
        _pluggy.RepeatCursor = true;
        await Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);
        _pluggy.RepeatCursor = false;
        _pluggy.RawNextJson = "\"https://pluggy.test/v2/transactions?secret-in-next=1\"";
        await Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);
        _pluggy.TransactionsStatus = HttpStatusCode.Gone;
        _pluggy.TransactionsErrorBody = FakePluggyServer.LegacyTransactionsGoneBody;
        await CodeOfAsync(() => Client().GetTransactionsAsync(auth, FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None));

        Assert.NotEmpty(_log.Lines);
        foreach (var forbidden in new[]
                 {
                     FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, "fake-api-key-", StoredSecret,
                     FakePluggyServer.ItemWithAccounts, FakePluggyServer.CheckingAccountId, FakePluggyServer.CheckingAccountNumber,
                     "cur+/", "cur%2B", "secret-in-next", FakePluggyServer.BaseUrl,
                     "Cantina Exemplo", "Corrida Exemplo", "COMPRA DEBITO", FakePluggyServer.MerchantCnpj, FakePluggyServer.RawOnlyMarker,
                     "58.90", "1175.66", "3500.00", "errorid",
                 })
        {
            Assert.DoesNotContain(_log.Lines, l => l.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void APluggyTransaction_NeverPrintsItsContent()
    {
        var transaction = new PluggyTransaction(
            "t-1", DateTime.UtcNow, -10m, "DEBIT", "BRL", "Cantina Exemplo", "RAW TEXT", null, null, "Cantina", null, null, null, null, null, null,
            "POSTED", null, """{"secret":"in-raw-json"}""");

        var text = transaction.ToString();

        Assert.DoesNotContain("Cantina", text, StringComparison.Ordinal);
        Assert.DoesNotContain("in-raw-json", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- issue #25: ask for a new reading of the item

    [Fact]
    public async Task RequestItemUpdate_PatchesTheItem_WithTheApiKeyOfTheConnection()
    {
        await Client().RequestItemUpdateAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None);

        var request = _pluggy.Requests.Last();
        Assert.Equal(("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"), (request.Method, request.Path));
        Assert.Equal("fake-api-key-1", request.ApiKey);
        Assert.Equal("{}", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, PluggyErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, PluggyErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, PluggyErrorCodes.ItemNotFound)]
    public async Task RequestItemUpdate_TranslatesTheStatusIntoAClosedCode(HttpStatusCode status, string expected)
    {
        _pluggy.ItemUpdateStatus = status;

        Assert.Equal(expected, await CodeOfAsync(() => Client().RequestItemUpdateAsync(Connection(), FakePluggyServer.ItemWithAccounts, CancellationToken.None)));
    }

    // ---------------------------------------------------------------- support

    /// <summary>One page of the cursor listing with one transaction; <paramref name="nextJson"/> null leaves "next" out.</summary>
    private static string Page(string transactionId, string? nextJson)
        => "{ \"results\": [ { \"id\": \"" + transactionId + "\", \"date\": \"2026-10-05T12:00:00.000Z\", \"amount\": -10 } ]"
           + (nextJson is null ? string.Empty : ", \"next\": " + nextJson) + " }";

    /// <summary>Hands out one key and answers the data calls with the given bodies, in order (the last one from then on).</summary>
    private sealed class PagesAnswer : HttpMessageHandler
    {
        private readonly string[] _bodies;

        public PagesAnswer(params string[] bodies) => _bodies = bodies;

        public int DataCalls => DataQueries.Count;

        public List<string> DataQueries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/auth")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"apiKey":"fake-api-key-pages"}""") });

            Assert.Equal("/v2/transactions", request.RequestUri.AbsolutePath);
            DataQueries.Add(request.RequestUri.Query);
            var body = _bodies[Math.Min(DataQueries.Count, _bodies.Length) - 1];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>Every line the client logs, as "Level: message".</summary>
    private sealed class ListLogger : ILogger<PluggyHttpClient>
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    /// <summary>Hands out one key and answers every data call with the same body.</summary>
    private sealed class FixedDataAnswer : HttpMessageHandler
    {
        private readonly string _body;

        public FixedDataAnswer(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/auth" ? """{"apiKey":"fake-api-key-fixed"}""" : _body),
            });
    }

    private sealed class AdjustableClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SingleHandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(PluggyHttpClient.HttpClientName, name);
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class FixedAnswer : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FixedAnswer(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    /// <summary>Hands out keys and then refuses every one of them.</summary>
    private sealed class AlwaysUnauthorizedData : HttpMessageHandler
    {
        public int AuthCalls { get; private set; }

        public int DataCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/auth")
            {
                AuthCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"apiKey":"fake-api-key-refused"}""") });
            }

            DataCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        }
    }
}
