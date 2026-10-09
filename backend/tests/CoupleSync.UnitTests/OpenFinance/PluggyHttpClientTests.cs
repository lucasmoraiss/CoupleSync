using System.Globalization;
using System.Net;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using CoupleSync.TestSupport;
using Microsoft.Extensions.Caching.Memory;
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

    private PluggyHttpClient Client(string baseUrl = FakePluggyServer.BaseUrl) => new(
        new SingleHandlerFactory(_pluggy),
        _cache,
        Options.Create(new OpenFinanceOptions { PluggyBaseUrl = baseUrl }),
        NullLogger<PluggyHttpClient>.Instance);

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
        var transactions = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        var request = _pluggy.Requests.Last();
        Assert.Equal(("GET", "/transactions"), (request.Method, request.Path));
        Assert.Equal(
            $"?accountId={FakePluggyServer.CheckingAccountId}&from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}&pageSize=500&page=1",
            request.Query);
        Assert.Equal("fake-api-key-1", request.ApiKey);

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
    public async Task GetTransactions_OfACreditCard_ReadsInstallmentsBillAndPendingStatus()
    {
        var transactions = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CreditCardAccountId, From, To, CancellationToken.None);

        var purchase = transactions.Single(t => t.Id == FakePluggyServer.CardPurchaseTransactionId);
        Assert.Equal(300.00m, purchase.Amount);
        Assert.Equal((2, 6), (purchase.InstallmentNumber, purchase.TotalInstallments));
        Assert.Equal("d1b2c3d4-0000-4000-8000-000000000001", purchase.BillId);
        Assert.Equal("PENDING", transactions.Single(t => t.Id == FakePluggyServer.CardPendingTransactionId).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetTransactions_ReadsEveryPageUntilTheLast(bool withoutTotalPages)
    {
        var today = DateTime.UtcNow.Date;
        _pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 1203)
            .Select(i => new FakeTransaction($"c1b2c3d4-0000-4000-8000-{i:D12}", today.AddDays(-1).AddSeconds(i), -i))
            .ToList();
        _pluggy.OmitTotalPages = withoutTotalPages;

        var transactions = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        Assert.Equal(1203, transactions.Count);
        Assert.Equal(1203, transactions.Select(t => t.Id).Distinct().Count());
        var pages = _pluggy.Requests.Where(r => r.Path == "/transactions").Select(r => r.Query[(r.Query.LastIndexOf('=') + 1)..]).ToList();
        Assert.Equal(new[] { "1", "2", "3" }, pages);
        Assert.Equal(1, _pluggy.AuthCalls);
    }

    [Fact]
    public async Task GetTransactions_OfAnAccountWithoutTransactions_IsAnEmptyList_InOneCall()
    {
        _pluggy.Transactions.Remove(FakePluggyServer.CheckingAccountId);

        var transactions = await Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None);

        Assert.Empty(transactions);
        Assert.Equal(1, _pluggy.Count("GET", "/transactions"));
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

            var transaction = Assert.Single(await Client().GetTransactionsAsync(
                Connection(), FakePluggyServer.CheckingAccountId, new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 7), CancellationToken.None));

            Assert.Equal(-1234.56m, transaction.Amount);
            Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), transaction.DateUtc);
            Assert.EndsWith("&from=2026-07-01&to=2026-10-07&pageSize=500&page=1", _pluggy.Requests.Last().Query, StringComparison.Ordinal);
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
            { "total": 4, "totalPages": 1, "page": 1, "results": [
              { "id": "t-ok", "date": "2026-10-05T12:00:00.000Z", "amount": -10 },
              { "date": "2026-10-05T12:00:00.000Z", "amount": -10 },
              { "id": "t-no-date", "amount": -10 },
              { "id": "t-no-amount", "date": "2026-10-05T12:00:00.000Z" },
              "not an object"
            ] }
            """);
        var client = new PluggyHttpClient(
            new SingleHandlerFactory(handler), _cache,
            Options.Create(new OpenFinanceOptions { PluggyBaseUrl = FakePluggyServer.BaseUrl }), NullLogger<PluggyHttpClient>.Instance);

        var transaction = Assert.Single(await client.GetTransactionsAsync(Connection(), "account", From, To, CancellationToken.None));

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
    public async Task GetTransactions_TranslatesTheStatusIntoAClosedCode(HttpStatusCode status, string expected)
    {
        _pluggy.TransactionsStatus = status;

        Assert.Equal(expected, await CodeOfAsync(() => Client().GetTransactionsAsync(Connection(), FakePluggyServer.CheckingAccountId, From, To, CancellationToken.None)));
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
