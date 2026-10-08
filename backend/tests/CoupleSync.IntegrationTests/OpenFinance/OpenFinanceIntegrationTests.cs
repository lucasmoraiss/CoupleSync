using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #24 — Open Finance phase 1: connection with Meu Pluggy (and issue #31: leaving the group takes the
/// person's Open Finance out of it). Pluggy is <see cref="FakePluggyServer"/>
/// (an HttpMessageHandler), the encryption key is generated per host, every credential and account is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceIntegrationTests
{
    private const string Base = "/api/v1/openfinance";

    private const string ChangedWhileVerifyingMessage = "Esta conexão mudou durante a verificação. Verifique de novo.";

    private const string EmptyItemMessage =
        "Nenhuma conta neste item. Confira se o conector MeuPluggy está ligado na aplicação e se a conexão foi feita pela Demo com a sua conta do Meu Pluggy.";

    private static object Credentials(string? clientId = null, string? clientSecret = null) => new
    {
        clientId = clientId ?? FakePluggyServer.ClientId,
        clientSecret = clientSecret ?? FakePluggyServer.ClientSecret,
    };

    private static object NewConnection(string label = "Bancos da Ana", int? historyMonths = 3) => new
    {
        label,
        clientId = FakePluggyServer.ClientId,
        clientSecret = FakePluggyServer.ClientSecret,
        historyMonths,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? message = null)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(code, body.GetProperty("code").GetString());
        var text = body.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        if (message is not null) Assert.Equal(message, text);
    }

    private static async Task<Guid> ConnectAsync(Member member, string label = "Bancos da Ana")
    {
        var created = await member.Client.PostAsJsonAsync($"{Base}/connections", NewConnection(label));
        Assert.True(HttpStatusCode.Created == created.StatusCode, await created.Content.ReadAsStringAsync());
        return (await JsonAsync(created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> AddItemAsync(Member member, Guid connectionId, string itemId)
    {
        var added = await member.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId });
        Assert.True(HttpStatusCode.OK == added.StatusCode, await added.Content.ReadAsStringAsync());
        return await JsonAsync(added);
    }

    // ---------------------------------------------------------------- availability (no key on the server)

    [Fact]
    public async Task WithoutTheEncryptionKey_TheApiStarts_StatusSaysUnavailable_AndEveryWriteAnswers503()
    {
        await using var factory = new OpenFinanceApiFactory(encryptionKey: null);
        var ana = await factory.RegisterAsync("Ana"); // the API started and works without the variable

        var health = await ana.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var status = await ana.Client.GetAsync($"{Base}/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await JsonAsync(status);
        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal(0, body.GetProperty("connections").GetArrayLength());

        const string unavailable = "OPENFINANCE_UNAVAILABLE";
        var id = Guid.NewGuid();
        await AssertErrorAsync(await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection()),
            HttpStatusCode.ServiceUnavailable, unavailable);
        await AssertErrorAsync(await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials()),
            HttpStatusCode.ServiceUnavailable, unavailable);
        await AssertErrorAsync(await ana.Client.PostAsJsonAsync($"{Base}/connections/{id}/items", new { itemId = FakePluggyServer.ItemWithAccounts }),
            HttpStatusCode.ServiceUnavailable, unavailable);
        await AssertErrorAsync(await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{id}", new { syncEnabled = false }),
            HttpStatusCode.ServiceUnavailable, unavailable);
        await AssertErrorAsync(await ana.Client.DeleteAsync($"{Base}/connections/{id}"),
            HttpStatusCode.ServiceUnavailable, unavailable);

        Assert.Empty(factory.Pluggy.Requests); // nothing reached Pluggy
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_connections"));
    }

    [Theory]
    [InlineData("not-base64-at-all!!")]
    [InlineData("c2hvcnQta2V5")] // valid Base64, but 9 bytes instead of 32
    public async Task AKeyThatIsNotBase64Of32Bytes_CountsAsAbsent_AndIsNotWrittenToTheLog(string badKey)
    {
        await using var factory = new OpenFinanceApiFactory(badKey);
        var ana = await factory.RegisterAsync("Ana");

        var body = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.False(body.GetProperty("available").GetBoolean());
        await AssertErrorAsync(await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection()),
            HttpStatusCode.ServiceUnavailable, "OPENFINANCE_UNAVAILABLE");

        Assert.Contains(factory.Logs.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal)
            && line.Contains("OPENFINANCE_ENCRYPTION_KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(badKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTheKey_StatusSaysAvailable()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var body = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");

        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Equal(0, body.GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task TheHost_RegistersThePluggyClientWithA30SecondTimeout_AndTheConfiguredAddress()
    {
        await using var factory = new OpenFinanceApiFactory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("Pluggy");
        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
        Assert.IsType<CoupleSync.Infrastructure.Integrations.Pluggy.PluggyHttpClient>(
            scope.ServiceProvider.GetRequiredService<CoupleSync.Application.Common.Interfaces.IPluggyClient>());
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<CoupleSync.Infrastructure.Integrations.Pluggy.OpenFinanceOptions>>().Value;
        Assert.Equal(FakePluggyServer.BaseUrl, options.PluggyBaseUrl);
    }

    [Fact]
    public async Task EveryRoute_RequiresASessionAndAGroup()
    {
        await using var factory = new OpenFinanceApiFactory();
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Base}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Base}/connections", NewConnection())).StatusCode);

        var register = await anonymous.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = $"of-{Guid.NewGuid():N}@example.com", Name = "Sem Grupo", Password = "SecurePass123!",
        });
        var token = (await JsonAsync(register)).GetProperty("accessToken").GetString()!;
        anonymous.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        await AssertErrorAsync(await anonymous.GetAsync($"{Base}/status"), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
    }

    // ---------------------------------------------------------------- credentials/test

    [Fact]
    public async Task TestCredentials_Valid_Answers200_AndStoresNothing()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("valid").GetBoolean());
        Assert.Equal(1, factory.Pluggy.AuthCalls);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_connections"));
    }

    [Fact]
    public async Task TestCredentials_Invalid_AnswersPluggyInvalidCredentials_InPortuguese()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials(clientSecret: "fake-wrong-secret"));

        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "PLUGGY_INVALID_CREDENTIALS",
            "O Pluggy recusou o Client ID ou o Client Secret. Confira os dois na aba \"Aplicação\" do dashboard.pluggy.ai e tente de novo.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_connections"));
    }

    [Theory]
    [InlineData("status-500")]
    [InlineData("status-503")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task TestCredentials_PluggyDown_AnswersPluggyUnavailable(string failure)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        switch (failure)
        {
            case "status-500": factory.Pluggy.AuthStatus = HttpStatusCode.InternalServerError; break;
            case "status-503": factory.Pluggy.AuthStatus = HttpStatusCode.ServiceUnavailable; break;
            case "network": factory.Pluggy.NetworkDown = true; break;
            default: factory.Pluggy.TimesOut = true; break;
        }

        var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());

        await AssertErrorAsync(response, HttpStatusCode.BadGateway, "PLUGGY_UNAVAILABLE",
            "O Pluggy não respondeu agora. Tente de novo em alguns minutos.");
    }

    [Fact]
    public async Task TestCredentials_PluggyRateLimit_AnswersPluggyRateLimited()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        factory.Pluggy.AuthStatus = HttpStatusCode.TooManyRequests;

        var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, "PLUGGY_RATE_LIMITED",
            "O Pluggy limitou as consultas por enquanto. Aguarde alguns minutos e tente de novo.");
    }

    [Fact]
    public async Task TestCredentials_IsLimitedTo5PerMinutePerUser()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());
            Assert.True(HttpStatusCode.OK == response.StatusCode, $"attempt {attempt}: {(int)response.StatusCode}");
        }

        var sixth = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());
        await AssertErrorAsync(sixth, HttpStatusCode.TooManyRequests, "RATE_LIMIT_EXCEEDED");
        Assert.Equal(5, factory.Pluggy.AuthCalls);

        // Creating a connection sends credentials to Pluggy too: same budget, so it is no way around the limit.
        var create = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection());
        await AssertErrorAsync(create, HttpStatusCode.TooManyRequests, "RATE_LIMIT_EXCEEDED");
        Assert.Equal(5, factory.Pluggy.AuthCalls);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_connections"));

        // Per user: the partner, from the same address, still gets through.
        var other = await bruno.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task TestCredentials_MissingField_Answers400InTheSingleFormat()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", new { clientId = FakePluggyServer.ClientId, clientSecret = "" });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.Empty(factory.Pluggy.Requests);
    }

    // ---------------------------------------------------------------- connections

    [Fact]
    public async Task CreateConnection_StoresTheCredentialsEncrypted_AndNeverShowsThem()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var created = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Bancos da Ana", historyMonths: 6));

        var createdRaw = await created.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.Created == created.StatusCode, createdRaw);
        var connection = JsonSerializer.Deserialize<JsonElement>(createdRaw);
        Assert.Equal("Bancos da Ana", connection.GetProperty("label").GetString());
        Assert.Equal("Active", connection.GetProperty("status").GetString());
        Assert.Equal(6, connection.GetProperty("historyMonths").GetInt32());
        // The hint is the last 4 characters of the client id, and nothing else of it.
        Assert.Equal(FakePluggyServer.ClientId[^4..], connection.GetProperty("clientIdHint").GetString());

        // In the database: neither value in clear text.
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        var storedSecret = Assert.IsType<string>(row["client_secret_encrypted"]);
        var storedClientId = Assert.IsType<string>(row["client_id_encrypted"]);
        Assert.NotEqual(FakePluggyServer.ClientSecret, storedSecret);
        Assert.NotEqual(FakePluggyServer.ClientId, storedClientId);
        Assert.Equal(FakePluggyServer.ClientId[^4..], row["client_id_hint"]);
        Assert.Equal("PLUGGY", row["provider"]);
        Assert.Equal(ana.UserId.ToString(), row["user_id"]!.ToString(), ignoreCase: true);
        foreach (var value in row.Values.OfType<string>())
        {
            Assert.DoesNotContain(FakePluggyServer.ClientSecret, value, StringComparison.Ordinal);
            Assert.DoesNotContain(FakePluggyServer.ClientId, value, StringComparison.Ordinal);
        }

        // Use the connection too, so that every route that touches the credentials has answered and logged.
        var accounts = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connection.GetProperty("id").GetGuid()}/items",
            new { itemId = FakePluggyServer.ItemWithAccounts });
        var statusRaw = await (await ana.Client.GetAsync($"{Base}/status")).Content.ReadAsStringAsync();
        foreach (var answer in new[] { createdRaw, statusRaw, await accounts.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain(FakePluggyServer.ClientSecret, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(FakePluggyServer.ClientId, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(storedSecret, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(storedClientId, answer, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-api-key", answer, StringComparison.Ordinal);
        }

        // In the log (every level, every category): no secret, no client id, no Pluggy API key.
        Assert.NotEmpty(factory.Logs.Lines);
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.ClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.ClientId, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains("fake-api-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateConnection_TwoValuesEncryptedWithTheSameKey_DoNotRepeat()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        await ConnectAsync(ana);
        await ConnectAsync(bruno, "Bancos do Bruno");

        var rows = await factory.RowsAsync("SELECT client_secret_encrypted FROM bank_connections");

        // Same secret, same key, two rows: a nonce per value makes the stored texts differ.
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0]["client_secret_encrypted"], rows[1]["client_secret_encrypted"]);
    }

    [Fact]
    public async Task CreateConnection_ASecondOneForTheSamePersonInTheGroup_Answers409()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        await ConnectAsync(ana);

        var second = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Outra"));

        await AssertErrorAsync(second, HttpStatusCode.Conflict, "BANK_CONNECTION_ALREADY_EXISTS");
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_connections"));
    }

    [Fact]
    public async Task CreateConnection_WithCredentialsPluggyRefuses_StoresNothing()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections", new
        {
            label = "Bancos", clientId = FakePluggyServer.ClientId, clientSecret = "fake-wrong-secret", historyMonths = 3,
        });

        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "PLUGGY_INVALID_CREDENTIALS");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_connections"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(24)]
    public async Task CreateConnection_HistoryMonthsOtherThan3_6_12_Answers400(int historyMonths)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection(historyMonths: historyMonths));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task CreateConnection_WithoutHistoryMonths_Uses3()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var created = await ana.Client.PostAsJsonAsync($"{Base}/connections", new
        {
            label = "Bancos", clientId = FakePluggyServer.ClientId, clientSecret = FakePluggyServer.ClientSecret,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(3, (await JsonAsync(created)).GetProperty("historyMonths").GetInt32());
    }

    // ---------------------------------------------------------------- items and accounts

    [Fact]
    public async Task AddItem_StoresTheItemAndItsAccounts_AndReturnsTheAccountsFound()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        var result = await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);

        Assert.Equal("Banco Exemplo", result.GetProperty("connectorName").GetString());
        var accounts = result.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(2, accounts.Count);
        var checking = accounts.Single(a => a.GetProperty("subtype").GetString() == "CHECKING_ACCOUNT");
        Assert.Equal("BANK", checking.GetProperty("type").GetString());
        Assert.Equal("Conta Corrente", checking.GetProperty("name").GetString());
        Assert.Equal("1234", checking.GetProperty("numberMasked").GetString());
        Assert.Equal(1234.56m, checking.GetProperty("balance").GetDecimal());
        Assert.Equal("BRL", checking.GetProperty("currency").GetString());
        Assert.True(checking.GetProperty("syncEnabled").GetBoolean());
        var card = accounts.Single(a => a.GetProperty("subtype").GetString() == "CREDIT_CARD");
        Assert.Equal("CREDIT", card.GetProperty("type").GetString());
        Assert.Equal("5678", card.GetProperty("numberMasked").GetString());
        Assert.Equal(5000m, card.GetProperty("creditLimit").GetDecimal());
        Assert.Equal(4012.35m, card.GetProperty("availableCreditLimit").GetDecimal());
        Assert.Equal(148.15m, card.GetProperty("minimumPayment").GetDecimal());
        Assert.Equal("2026-10-20", card.GetProperty("balanceCloseDate").GetString());
        Assert.Equal("2026-10-27", card.GetProperty("balanceDueDate").GetString());
        Assert.Equal("MASTERCARD", card.GetProperty("brand").GetString());

        // Stored: one item, two accounts, and the account number only masked.
        var item = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_items"));
        Assert.Equal(FakePluggyServer.ItemWithAccounts, item["pluggy_item_id"]);
        Assert.Equal("UPDATED", item["status"]);
        Assert.Equal("SUCCESS", item["execution_status"]);
        var stored = await factory.RowsAsync("SELECT * FROM bank_accounts");
        Assert.Equal(2, stored.Count);
        foreach (var value in stored.SelectMany(r => r.Values).OfType<string>())
        {
            Assert.DoesNotContain("98765", value, StringComparison.Ordinal);
            Assert.DoesNotContain(FakePluggyServer.CreditCardNumber, value, StringComparison.Ordinal);
        }

        // And the status shows the same to the group.
        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.Equal("Ana", connection.GetProperty("userName").GetString());
        Assert.Equal(ana.UserId, connection.GetProperty("userId").GetGuid());
        Assert.True(connection.GetProperty("isMine").GetBoolean());
        var statusItem = Assert.Single(connection.GetProperty("items").EnumerateArray());
        Assert.Equal("Banco Exemplo", statusItem.GetProperty("connectorName").GetString());
        Assert.Equal(2, statusItem.GetProperty("accounts").GetArrayLength());
        Assert.All(statusItem.GetProperty("accounts").EnumerateArray(), a => Assert.True(a.GetProperty("syncEnabled").GetBoolean()));
    }

    [Fact]
    public async Task AddItem_WithoutAccounts_Answers422PluggyItemEmpty_AndStoresNothing()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.EmptyItem });

        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "PLUGGY_ITEM_EMPTY", EmptyItemMessage);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_accounts"));
    }

    [Fact]
    public async Task AddItem_ThatPluggyDoesNotKnow_AnswersPluggyItemNotFound()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.UnknownItem });

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "PLUGGY_ITEM_NOT_FOUND",
            "O Pluggy não encontrou este Item ID. Copie de novo pelo menu de três pontos da conexão, na Demo da sua aplicação.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
    }

    [Theory]
    [InlineData(FakePluggyServer.ItemWithLoginError)] // LOGIN_ERROR
    [InlineData(FakePluggyServer.ItemWaitingUserInput)] // WAITING_USER_INPUT
    public async Task AddItem_ThatNeedsTheUserAtTheBank_AnswersPluggyItemNeedsAction_AndStoresNothing(string itemId)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId });

        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "PLUGGY_ITEM_NEEDS_ACTION",
            "Este banco precisa de uma ação sua no Meu Pluggy (entrar de novo ou autorizar). Resolva em meu.pluggy.ai e verifique outra vez.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_accounts"));
        Assert.Equal("Active", Assert.Single(await factory.RowsAsync("SELECT status FROM bank_connections"))["status"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../accounts")]
    [InlineData("item id with spaces")]
    public async Task AddItem_WithAnItemIdThatIsNotAnIdentifier_Answers400_WithoutCallingPluggy(string itemId)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        var callsBefore = factory.Pluggy.Requests.Count;

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.Equal(callsBefore, factory.Pluggy.Requests.Count);
    }

    [Fact]
    public async Task AddItem_Twice_UpdatesInsteadOfDuplicating_AndKeepsTheSyncChoice()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        var first = await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        var cardId = first.GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("subtype").GetString() == "CREDIT_CARD").GetProperty("id").GetGuid();
        var off = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{cardId}", new { syncEnabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var second = await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);

        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM bank_accounts")).Count);
        var card = second.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == cardId);
        Assert.False(card.GetProperty("syncEnabled").GetBoolean());
    }

    [Fact]
    public async Task AddItem_AlreadyConnectedBySomeoneElse_Answers409()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var carla = await factory.RegisterAsync("Carla"); // another group
        await AddItemAsync(ana, await ConnectAsync(ana), FakePluggyServer.ItemWithAccounts);
        var brunoConnection = await ConnectAsync(bruno, "Bancos do Bruno");
        var carlaConnection = await ConnectAsync(carla, "Bancos da Carla");

        var sameGroup = await bruno.Client.PostAsJsonAsync($"{Base}/connections/{brunoConnection}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        var otherGroup = await carla.Client.PostAsJsonAsync($"{Base}/connections/{carlaConnection}/items", new { itemId = FakePluggyServer.ItemWithAccounts });

        await AssertErrorAsync(sameGroup, HttpStatusCode.Conflict, "BANK_ITEM_ALREADY_CONNECTED");
        await AssertErrorAsync(otherGroup, HttpStatusCode.Conflict, "BANK_ITEM_ALREADY_CONNECTED");
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM bank_accounts")).Count);
    }

    [Fact]
    public async Task AddItem_ReusesThePluggyApiKeyOfTheConnection_AndAuthenticatesAgainWhenItExpires()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        var authAfterCreate = factory.Pluggy.AuthCalls;

        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);

        // One authentication for the two verifications (4 data calls): the key is cached per connection.
        Assert.Equal(authAfterCreate + 1, factory.Pluggy.AuthCalls);

        // The key stops being accepted (401): the client authenticates once more and repeats the call.
        factory.Pluggy.ExpireIssuedKeys();
        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        Assert.Equal(authAfterCreate + 2, factory.Pluggy.AuthCalls);
    }

    [Fact]
    public async Task AddItem_WhenTheStoredCredentialsStoppedWorking_MarksTheConnectionWithTheError()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        factory.Pluggy.AuthStatus = HttpStatusCode.Unauthorized; // the secret was regenerated in the dashboard

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });

        // The answer and what the group reads on the connection name the way out that exists: trying again would
        // send the same stored credentials.
        const string wayOut = "O Pluggy recusou as credenciais guardadas nesta conexão. Desconecte e conecte de novo com o Client ID e o Client Secret certos.";
        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "PLUGGY_INVALID_CREDENTIALS", wayOut);
        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.Equal("Error", connection.GetProperty("status").GetString());
        Assert.Equal("PLUGGY_INVALID_CREDENTIALS", connection.GetProperty("lastErrorCode").GetString());
        Assert.Equal(wayOut, connection.GetProperty("lastErrorMessage").GetString());

        // Working again: the mark goes away.
        factory.Pluggy.AuthStatus = null;
        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.Equal("Active", connection.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("lastErrorCode").ValueKind);
    }

    // ---------------------------------------------------------------- who sees, who edits

    [Fact]
    public async Task AnotherGroup_DoesNotSeeNorChangeTheConnection_404()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        var added = await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        var accountId = added.GetProperty("accounts")[0].GetProperty("id").GetGuid();
        var outsider = await factory.RegisterAsync("Carla");

        var status = await outsider.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.Equal(0, status.GetProperty("connections").GetArrayLength());

        await AssertErrorAsync(await outsider.Client.DeleteAsync($"{Base}/connections/{connectionId}"),
            HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND");
        await AssertErrorAsync(await outsider.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false }),
            HttpStatusCode.NotFound, "BANK_ACCOUNT_NOT_FOUND");
        await AssertErrorAsync(await outsider.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.OtherItemWithAccounts }),
            HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND");

        // Nothing changed for the owner.
        var row = Assert.Single(await factory.RowsAsync("SELECT status, client_secret_encrypted FROM bank_connections"));
        Assert.Equal("Active", row["status"]);
        Assert.NotNull(row["client_secret_encrypted"]);
        Assert.All(await factory.RowsAsync("SELECT sync_enabled FROM bank_accounts"), r => Assert.Equal(1L, r["sync_enabled"]));
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_items"));
    }

    [Fact]
    public async Task AMemberOfTheSameGroup_SeesEverything_ButGets403OnDisconnectSyncAndAddItem()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(ana);
        var added = await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        var accountId = added.GetProperty("accounts")[0].GetProperty("id").GetGuid();

        var status = await bruno.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.Equal("Bancos da Ana", connection.GetProperty("label").GetString());
        Assert.Equal("Ana", connection.GetProperty("userName").GetString());
        Assert.False(connection.GetProperty("isMine").GetBoolean());
        Assert.Equal(2, Assert.Single(connection.GetProperty("items").EnumerateArray()).GetProperty("accounts").GetArrayLength());

        await AssertErrorAsync(await bruno.Client.DeleteAsync($"{Base}/connections/{connectionId}"),
            HttpStatusCode.Forbidden, "BANK_CONNECTION_FORBIDDEN");
        await AssertErrorAsync(await bruno.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false }),
            HttpStatusCode.Forbidden, "BANK_CONNECTION_FORBIDDEN");
        await AssertErrorAsync(await bruno.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.OtherItemWithAccounts }),
            HttpStatusCode.Forbidden, "BANK_CONNECTION_FORBIDDEN");

        var row = Assert.Single(await factory.RowsAsync("SELECT status FROM bank_connections"));
        Assert.Equal("Active", row["status"]);
        Assert.All(await factory.RowsAsync("SELECT sync_enabled FROM bank_accounts"), r => Assert.Equal(1L, r["sync_enabled"]));
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_items"));
    }

    [Fact]
    public async Task TheOwner_TurnsTheSyncOfAnAccountOffAndOn()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var added = await AddItemAsync(ana, await ConnectAsync(ana), FakePluggyServer.ItemWithAccounts);
        var accountId = added.GetProperty("accounts")[0].GetProperty("id").GetGuid();

        var off = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false });

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await JsonAsync(off)).GetProperty("syncEnabled").GetBoolean());
        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var accounts = status.GetProperty("connections")[0].GetProperty("items")[0].GetProperty("accounts").EnumerateArray().ToList();
        Assert.False(accounts.Single(a => a.GetProperty("id").GetGuid() == accountId).GetProperty("syncEnabled").GetBoolean());
        Assert.True(accounts.Single(a => a.GetProperty("id").GetGuid() != accountId).GetProperty("syncEnabled").GetBoolean());

        var on = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = true });
        Assert.True((await JsonAsync(on)).GetProperty("syncEnabled").GetBoolean());
    }

    [Fact]
    public async Task PatchAccount_WithoutSyncEnabled_Answers400()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var added = await AddItemAsync(ana, await ConnectAsync(ana), FakePluggyServer.ItemWithAccounts);
        var accountId = added.GetProperty("accounts")[0].GetProperty("id").GetGuid();

        var response = await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }

    // ---------------------------------------------------------------- disconnect

    [Fact]
    public async Task Disconnect_ErasesTheCredentialsAtOnce_KeepsItemsAndAccounts_AndAllowsConnectingAgain()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);

        var deleted = await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Disconnected", row["status"]);
        Assert.Null(row["client_secret_encrypted"]);
        Assert.Null(row["client_id_encrypted"]);
        Assert.Null(row["client_id_hint"]);
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM bank_accounts")).Count);

        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.Equal("Disconnected", connection.GetProperty("status").GetString());
        Assert.Equal(2, connection.GetProperty("items")[0].GetProperty("accounts").GetArrayLength());

        // Without credentials nothing can be verified any more...
        var pluggyCalls = factory.Pluggy.Requests.Count;
        await AssertErrorAsync(
            await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.OtherItemWithAccounts }),
            HttpStatusCode.Conflict, "BANK_CONNECTION_DISCONNECTED");
        Assert.Equal(pluggyCalls, factory.Pluggy.Requests.Count);
        // ...disconnecting again changes nothing...
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);

        // ...and the person can connect again: it is the same connection, with new credentials.
        var again = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("De volta", historyMonths: 12));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var reconnected = await JsonAsync(again);
        Assert.Equal(connectionId, reconnected.GetProperty("id").GetGuid());
        Assert.Equal("Active", reconnected.GetProperty("status").GetString());
        Assert.Equal("De volta", reconnected.GetProperty("label").GetString());
        row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.NotNull(row["client_secret_encrypted"]);
        Assert.Equal(12L, row["history_months"]);
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);
    }

    // ---------------------------------------------------------------- review round 1

    [Fact]
    public async Task TestCredentials_AcceptedOrRefused_NeverWritesTheCredentialsToTheLog()
    {
        const string wrongSecret = "fake-wrong-secret-0f1e2d3c";
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");

        var accepted = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());
        var refused = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials(clientSecret: wrongSecret));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await AssertErrorAsync(refused, HttpStatusCode.UnprocessableEntity, "PLUGGY_INVALID_CREDENTIALS");
        foreach (var answer in new[] { await accepted.Content.ReadAsStringAsync(), await refused.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain(FakePluggyServer.ClientSecret, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongSecret, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(FakePluggyServer.ClientId, answer, StringComparison.Ordinal);
        }

        // The log was really captured down to Trace: the action of this route and the Pluggy HTTP client are in it.
        Assert.Contains(factory.Logs.Lines, line => line.StartsWith("Trace", StringComparison.Ordinal)
            && line.Contains("TestCredentials", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Lines, line => line.StartsWith("Trace System.Net.Http.HttpClient.Pluggy", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.ClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(wrongSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.ClientId, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains("fake-api-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddItem_WhenTheStoredCredentialsWereEncryptedWithAnotherKey_Answers422_WithoutCallingPluggy()
    {
        // What another key wrote: the stored secret of a server with a different OPENFINANCE_ENCRYPTION_KEY.
        string foreignSecret;
        await using (var otherServer = new OpenFinanceApiFactory())
        {
            await ConnectAsync(await otherServer.RegisterAsync("Outra"));
            foreignSecret = Assert.IsType<string>(
                Assert.Single(await otherServer.RowsAsync("SELECT client_secret_encrypted FROM bank_connections"))["client_secret_encrypted"]);
        }

        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        await factory.ExecuteAsync("UPDATE bank_connections SET client_secret_encrypted = $secret", ("$secret", foreignSecret));
        var before = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        var pluggyCalls = factory.Pluggy.Requests.Count;

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });

        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "OPENFINANCE_CREDENTIALS_UNREADABLE",
            "Não foi possível ler as credenciais guardadas. Desconecte e conecte de novo com o Client ID e o Client Secret.");
        Assert.Equal(pluggyCalls, factory.Pluggy.Requests.Count); // nothing was sent to Pluggy
        var after = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", after["status"]);
        Assert.Equal(before, after); // the connection is exactly as it was
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));

        // The way out the message names works: disconnect, connect again.
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        await ConnectAsync(ana);
        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
    }

    [Theory]
    [InlineData(false)] // Pluggy answers with the item and its accounts
    [InlineData(true)] // Pluggy refuses the credentials the verification used
    public async Task DisconnectWhileAnItemIsBeingVerified_TheVerificationAnswers409_AndWritesNothing(bool pluggyRefuses)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        if (pluggyRefuses) factory.Pluggy.DataStatus = HttpStatusCode.Forbidden;

        // Pluggy holds its answer about the item until the test lets it go.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (!request.Path.StartsWith("/items/", StringComparison.Ordinal)) return;
            asked.TrySetResult();
            await answerNow.Task;
        };

        var verifying = ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var disconnected = await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}");
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        answerNow.SetResult();

        await AssertErrorAsync(await verifying, HttpStatusCode.Conflict, "BANK_CONNECTION_CHANGED", ChangedWhileVerifyingMessage);
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Disconnected", row["status"]);
        Assert.Null(row["client_secret_encrypted"]);
        Assert.Null(row["client_id_encrypted"]);
        Assert.Null(row["client_id_hint"]);
        Assert.Null(row["last_error_code"]);
        Assert.Null(row["last_error_message"]);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_accounts"));

        // Verifying again says what happened and the way out...
        factory.Pluggy.BeforeAnswer = null;
        factory.Pluggy.DataStatus = null;
        await AssertErrorAsync(
            await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts }),
            HttpStatusCode.Conflict, "BANK_CONNECTION_DISCONNECTED",
            "Esta conexão foi desconectada. Conecte de novo com o Client ID e o Client Secret para adicionar bancos.");
        // ...and nothing is stuck: the person connects again and the same item is verified.
        await ConnectAsync(ana);
        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
    }

    [Fact]
    public async Task ConnectAgainWhileAnItemIsBeingVerified_TheVerificationAnswers409Changed_AndThePluggyKeyItGotIsNotKept()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        // Pluggy holds its answer about the item until the test lets it go.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (!request.Path.StartsWith("/items/", StringComparison.Ordinal)) return;
            asked.TrySetResult();
            await answerNow.Task;
        };

        var verifying = ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Meanwhile the person disconnects and connects again: the connection is active, with credentials stored anew.
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        Assert.Equal(connectionId, await ConnectAsync(ana, "De volta"));
        answerNow.SetResult();

        // The text is true here too: it does not say "disconnected" nor send the person to connect again.
        await AssertErrorAsync(await verifying, HttpStatusCode.Conflict, "BANK_CONNECTION_CHANGED", ChangedWhileVerifyingMessage);
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", row["status"]);
        Assert.Equal("De volta", row["label"]);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_accounts"));

        // The old verification asked Pluggy for an API key with the credentials it still had in memory. That key is
        // not kept for the connection: the next verification authenticates with the credentials stored now.
        var authCalls = factory.Pluggy.AuthCalls;
        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        Assert.Equal(authCalls + 1, factory.Pluggy.AuthCalls);
    }

    [Fact]
    public async Task Disconnect_WhenTheCredentialsChangeUnderEveryAttempt_Answers409InPortuguese_AndTheNextTryErasesThem()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        // Every save of the request meets yet another change of the stored credentials.
        var collisions = 0;
        Task Collide()
        {
            collisions++;
            factory.BeforeNextSave = Collide;
            return factory.ExecuteAsync(
                $"UPDATE bank_connections SET client_id_encrypted = 'other-ciphertext-{collisions}', client_secret_encrypted = 'other-ciphertext-{collisions}'");
        }

        factory.BeforeNextSave = Collide;

        var response = await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}");

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "BANK_CONNECTION_CHANGED",
            "Esta conexão mudou enquanto era desconectada. Tente desconectar de novo.");
        Assert.Equal(3, collisions);
        // Nothing half-written: the row is what the last of the other requests stored.
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Active", row["status"]);
        Assert.Equal("other-ciphertext-3", row["client_secret_encrypted"]);

        // Trying again, as the message says, erases the credentials.
        factory.BeforeNextSave = null;
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Disconnected", row["status"]);
        Assert.Null(row["client_secret_encrypted"]);
        Assert.Null(row["client_id_encrypted"]);
    }

    [Theory]
    [InlineData(false)] // the other request disconnected too
    [InlineData(true)] // the other request disconnected and connected again, with other credentials
    public async Task Disconnect_WhenAnotherRequestChangedTheCredentialsFirst_StillErasesThem_204(bool reconnected)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        factory.BeforeNextSave = () => reconnected
            ? factory.ExecuteAsync("UPDATE bank_connections SET client_id_encrypted = 'other-ciphertext', client_secret_encrypted = 'other-ciphertext'")
            : factory.ExecuteAsync("UPDATE bank_connections SET client_id_encrypted = NULL, client_secret_encrypted = NULL, client_id_hint = NULL, status = 'Disconnected'");

        var response = await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}");

        Assert.True(HttpStatusCode.NoContent == response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.Null(factory.BeforeNextSave); // the other write did happen in the middle
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("Disconnected", row["status"]);
        Assert.Null(row["client_secret_encrypted"]);
        Assert.Null(row["client_id_encrypted"]);
        Assert.Null(row["client_id_hint"]);
    }

    [Fact]
    public async Task ConnectAgain_WhenAnotherRequestConnectedFirst_Answers409_AndKeepsWhatWasStored()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        // The other request stores its credentials while Pluggy is answering this one (the save itself now runs in the
        // transaction that reads the membership again, where a second SQLite connection cannot write).
        factory.Pluggy.BeforeAnswer = _ => factory.ExecuteAsync(
            "UPDATE bank_connections SET client_id_encrypted = 'other-ciphertext', client_secret_encrypted = 'other-ciphertext', status = 'Active', label = 'A outra'");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Esta"));

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "BANK_CONNECTION_ALREADY_EXISTS");
        var row = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_connections"));
        Assert.Equal("A outra", row["label"]);
        Assert.Equal("other-ciphertext", row["client_secret_encrypted"]);
    }

    // ---------------------------------------------------------------- leaving the group (issue #31)

    private static async Task AssertNothingStoredAsync(OpenFinanceApiFactory factory)
    {
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_accounts"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_connections"));
    }

    /// <summary>The same person in a group of their own, created now: a client carrying the token of that group.</summary>
    private static async Task<Member> NewGroupAsync(OpenFinanceApiFactory factory, Member member, string? accessToken = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = accessToken is null
            ? member.Client.DefaultRequestHeaders.Authorization
            : new AuthenticationHeaderValue("Bearer", accessToken);
        var created = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var group = await JsonAsync(created);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", group.GetProperty("accessToken").GetString()!);
        return new Member(client, member.UserId, group.GetProperty("coupleId").GetGuid(), group.GetProperty("joinCode").GetString()!);
    }

    [Fact]
    public async Task WhenWhoConnectedLeavesTheGroup_TheirConnectionItemsAndAccountsAreDeleted_AndThePartnerSeesNothingOfThem()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        var added = await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);
        var accountId = added.GetProperty("accounts")[0].GetProperty("id").GetGuid();
        Assert.Single((await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status")).GetProperty("connections").EnumerateArray());

        var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

        // The answer of leaving is what it always was.
        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        var body = await JsonAsync(left);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeCoupleId").ValueKind);

        // The partner sees nothing of who left...
        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(0, status.GetProperty("connections").GetArrayLength());
        // ...because nothing is stored any more: connection (with the credentials), item and accounts.
        await AssertNothingStoredAsync(factory);

        // Who left (the token still names the group): 403 on every route, nothing reaches Pluggy.
        var pluggyCalls = factory.Pluggy.Requests.Count;
        await AssertErrorAsync(await bruno.Client.GetAsync($"{Base}/status"), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        await AssertErrorAsync(await bruno.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.OtherItemWithAccounts }),
            HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        await AssertErrorAsync(await bruno.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false }),
            HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        await AssertErrorAsync(await bruno.Client.DeleteAsync($"{Base}/connections/{connectionId}"),
            HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        Assert.Equal(pluggyCalls, factory.Pluggy.Requests.Count);
    }

    [Fact]
    public async Task WhenTheOwnerRemovesWhoConnected_TheirConnectionItemsAndAccountsAreDeleted_AndTheGroupSeesNothingOfThem()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);

        var removed = await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}");

        // The answer of removing is what it always was.
        Assert.True(HttpStatusCode.NoContent == removed.StatusCode, await removed.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, await removed.Content.ReadAsStringAsync());
        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.Equal(0, status.GetProperty("connections").GetArrayLength());
        await AssertNothingStoredAsync(factory);
        await AssertErrorAsync(await bruno.Client.GetAsync($"{Base}/status"), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
    }

    [Fact]
    public async Task Leaving_KeepsTheConnectionOfTheOtherMember_AndTheLeaversOwnConnectionInAnotherGroup()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var brunoElsewhere = await NewGroupAsync(factory, bruno);
        var anaConnection = await ConnectAsync(ana, "Bancos da Ana");
        await AddItemAsync(ana, anaConnection, FakePluggyServer.ItemWithAccounts);
        var brunoConnection = await ConnectAsync(bruno, "Bancos do Bruno");
        var brunoElsewhereConnection = await ConnectAsync(brunoElsewhere, "Bancos do Bruno, outro grupo");
        await AddItemAsync(brunoElsewhere, brunoElsewhereConnection, FakePluggyServer.OtherItemWithAccounts);
        const string accountsSql = "SELECT id, item_id, pluggy_account_id, sync_enabled FROM bank_accounts ORDER BY id";
        const string itemsSql = "SELECT id, connection_id, pluggy_item_id FROM bank_items ORDER BY id";
        var accountsBefore = await factory.RowsAsync(accountsSql);
        var itemsBefore = await factory.RowsAsync(itemsSql);
        Assert.Equal(3, accountsBefore.Count);
        Assert.Equal(2, itemsBefore.Count);

        // Bruno leaves Ana's group by its id, while working in his other group (the token names the other one).
        var left = await brunoElsewhere.Client.PostAsJsonAsync($"/api/v1/couples/{ana.CoupleId}/leave", new { });

        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        // Only his connection in the group he left is gone.
        var remaining = (await factory.RowsAsync("SELECT id FROM bank_connections")).Select(r => Guid.Parse(r["id"]!.ToString()!)).ToList();
        Assert.DoesNotContain(brunoConnection, remaining);
        Assert.Equal(new[] { anaConnection, brunoElsewhereConnection }.Order(), remaining.Order());
        Assert.Equal(itemsBefore, await factory.RowsAsync(itemsSql));
        Assert.Equal(accountsBefore, await factory.RowsAsync(accountsSql));

        // Ana still sees and uses hers, with the credentials that were stored.
        var forAna = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var hers = Assert.Single(forAna.GetProperty("connections").EnumerateArray());
        Assert.Equal(anaConnection, hers.GetProperty("id").GetGuid());
        Assert.True(hers.GetProperty("isMine").GetBoolean());
        Assert.Equal(2, Assert.Single(hers.GetProperty("items").EnumerateArray()).GetProperty("accounts").GetArrayLength());
        await AddItemAsync(ana, anaConnection, FakePluggyServer.ItemWithAccounts);

        // Bruno, in his other group, the same.
        var forBruno = await brunoElsewhere.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var his = Assert.Single(forBruno.GetProperty("connections").EnumerateArray());
        Assert.Equal(brunoElsewhereConnection, his.GetProperty("id").GetGuid());
        Assert.Equal("Active", his.GetProperty("status").GetString());
        await AddItemAsync(brunoElsewhere, brunoElsewhereConnection, FakePluggyServer.OtherItemWithAccounts);
    }

    [Fact]
    public async Task AfterLeaving_TheSameItemIdCanBeConnectedInAnotherGroup()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);

        var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        var brunoAlone = await NewGroupAsync(factory, bruno, (await JsonAsync(left)).GetProperty("accessToken").GetString());

        var newConnection = await ConnectAsync(brunoAlone, "Bancos do Bruno");
        var added = await AddItemAsync(brunoAlone, newConnection, FakePluggyServer.ItemWithAccounts);

        Assert.NotEqual(connectionId, newConnection);
        Assert.Equal(2, added.GetProperty("accounts").GetArrayLength());
        var item = Assert.Single(await factory.RowsAsync("SELECT connection_id, couple_id FROM bank_items"));
        Assert.Equal(newConnection, Guid.Parse(item["connection_id"]!.ToString()!));
        Assert.Equal(brunoAlone.CoupleId, Guid.Parse(item["couple_id"]!.ToString()!));
        // Ana's group has nothing of it.
        Assert.Equal(0, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status")).GetProperty("connections").GetArrayLength());
    }

    [Theory]
    [InlineData(false)] // an item verified for the first time
    [InlineData(true)] // an item already stored, verified again
    public async Task LeavingWhileAnItemIsBeingVerified_TheVerificationAnswers409_AndNothingIsWrittenBack(bool itemAlreadyStored)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        if (itemAlreadyStored) await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);

        // Pluggy holds its answer about the item until the test lets it go.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (!request.Path.StartsWith("/items/", StringComparison.Ordinal)) return;
            asked.TrySetResult();
            await answerNow.Task;
        };

        var verifying = bruno.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        await AssertNothingStoredAsync(factory);
        answerNow.SetResult();

        await AssertErrorAsync(await verifying, HttpStatusCode.Conflict, "BANK_CONNECTION_CHANGED", ChangedWhileVerifyingMessage);
        await AssertNothingStoredAsync(factory);
    }

    [Theory]
    [InlineData(false, false)] // the person leaves; a first connection
    [InlineData(true, false)] // the owner removes the person; a first connection
    [InlineData(false, true)] // the person leaves; connecting again a connection that was disconnected
    [InlineData(true, true)] // the owner removes the person; connecting again a connection that was disconnected
    public async Task ConnectingWhileThePersonLeavesOrIsRemoved_IsRefusedWith403_AndNoConnectionStaysInTheGroup(bool removedByTheOwner, bool connectingAgain)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        if (connectingAgain)
        {
            var old = await ConnectAsync(bruno, "Bancos do Bruno");
            await AddItemAsync(bruno, old, FakePluggyServer.ItemWithAccounts);
            Assert.Equal(HttpStatusCode.NoContent, (await bruno.Client.DeleteAsync($"{Base}/connections/{old}")).StatusCode);
        }

        // Pluggy holds its answer about the credentials until the test lets it go.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pluggy.BeforeAnswer = async request =>
        {
            if (request.Path != "/auth") return;
            asked.TrySetResult();
            await answerNow.Task;
        };

        var connecting = bruno.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Bancos do Bruno"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());
        await AssertNothingStoredAsync(factory);
        answerNow.SetResult();

        // The same answer every Open Finance route gives to who is no longer in the group, and nothing was stored.
        await AssertErrorAsync(await connecting, HttpStatusCode.Forbidden, "COUPLE_REQUIRED", "Você não faz mais parte deste grupo.");
        await AssertNothingStoredAsync(factory);
        Assert.Equal(0, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status")).GetProperty("connections").GetArrayLength());
    }

    [Theory]
    [InlineData(false)] // the person leaves
    [InlineData(true)] // the owner removes the person
    public async Task LeavingOrBeingRemoved_WithTheConnectionAlreadyDisconnected_DeletesTheConnectionItsItemsAndAccounts(bool removedByTheOwner)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);
        Assert.Equal(HttpStatusCode.NoContent, (await bruno.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        // Disconnected: no credentials, and the item and its accounts still there for the group.
        var stored = Assert.Single(await factory.RowsAsync("SELECT status, client_id_encrypted, client_secret_encrypted FROM bank_connections"));
        Assert.Equal("Disconnected", stored["status"]);
        Assert.Null(stored["client_id_encrypted"]);
        Assert.Null(stored["client_secret_encrypted"]);
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_items"));
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM bank_accounts")).Count);

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

        Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());
        Assert.Single(await factory.RowsAsync("SELECT user_id FROM couple_members"));
        await AssertNothingStoredAsync(factory);
        Assert.Equal(0, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status")).GetProperty("connections").GetArrayLength());
    }

    [Theory]
    [InlineData(false)] // the person leaves
    [InlineData(true)] // the owner removes the person
    public async Task TurningAnAccountOnOrOff_WhenTheExitDeletedItMeanwhile_Answers404_NotAServerError(bool removedByTheOwner)
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        var added = await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);
        var accountId = added.GetProperty("accounts")[0].GetProperty("id").GetGuid();
        // The request has read the account and is about to save when the exit goes through.
        factory.BeforeNextSave = async () =>
        {
            var exit = removedByTheOwner
                ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
                : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
            Assert.True(exit.IsSuccessStatusCode, await exit.Content.ReadAsStringAsync());
        };

        var response = await bruno.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false });

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "BANK_ACCOUNT_NOT_FOUND", "Conta bancária não encontrada.");
        Assert.Null(factory.BeforeNextSave); // the exit did happen in the middle
        await AssertNothingStoredAsync(factory);
    }

    [Fact]
    public async Task AWriteRefusedByAForeignKey_LeavesTheRepositoryAsAForeignKeyViolation()
    {
        // What a verification meets when the connection it writes under was deleted meanwhile (see the test above;
        // on PostgreSQL the foreign key is what answers, CoupleSync.PostgresTests proves that side).
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBankConnectionRepository>();
        var connectionThatIsNotThere = Guid.NewGuid();
        await repository.AddItemAsync(
            BankItem.Create(ana.CoupleId, connectionThatIsNotThere, FakePluggyServer.ItemWithAccounts, "Banco Exemplo", "UPDATED", null, null, null, DateTime.UtcNow),
            default);

        await Assert.ThrowsAsync<ForeignKeyViolationException>(() => repository.SaveChangesAsync(default));

        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
    }

    [Fact]
    public async Task AConnectionLeftBehindByWhoIsNoLongerInTheGroup_IsStillListedUnderANameThatIsNoOnes()
    {
        // Data from before issue #31 (someone connected and then left, while leaving still kept the connection):
        // nothing cleans it up, and the group keeps reading its own status.
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, connectionId, FakePluggyServer.ItemWithAccounts);
        await factory.ExecuteAsync("DELETE FROM couple_members WHERE user_id = @user", ("@user", bruno.UserId));
        Assert.Single(await factory.RowsAsync("SELECT user_id FROM couple_members"));

        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");

        var connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.Equal(connectionId, connection.GetProperty("id").GetGuid());
        Assert.Equal("Pessoa que saiu do grupo", connection.GetProperty("userName").GetString());
        Assert.False(connection.GetProperty("isMine").GetBoolean());
        Assert.Equal(2, Assert.Single(connection.GetProperty("items").EnumerateArray()).GetProperty("accounts").GetArrayLength());
        await AssertErrorAsync(await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}"),
            HttpStatusCode.Forbidden, "BANK_CONNECTION_FORBIDDEN");
    }

    [Fact]
    public async Task WithTheLogLevelsOfProduction_TheItemIdNeverReachesTheLog()
    {
        await using var factory = new OpenFinanceApiFactory { ProductionLogLevels = true };
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        await AddItemAsync(ana, connectionId, FakePluggyServer.ItemWithAccounts);
        var unknown = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.UnknownItem });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(2, factory.Pluggy.Requests.Count(r => r.Path.StartsWith("/items/", StringComparison.Ordinal)));
        // Information is on, as in production...
        Assert.Contains(factory.Logs.Lines, line => line.StartsWith("Information", StringComparison.Ordinal));
        // ...and the address of the calls to Pluggy (which carries the Item ID) is not written.
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.ItemWithAccounts, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.UnknownItem, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, line => line.Contains(FakePluggyServer.BaseUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCredentialsGoToPluggy_WithAContentLength_NotInChunks()
    {
        // Over a real socket, through the HTTP handler the application registers: what is on the wire.
        await using var pluggy = new RedirectingPluggyServer();
        await using var factory = new OpenFinanceApiFactory { LoopbackPluggyAddress = pluggy.Address };
        var ana = await factory.RegisterAsync("Ana");

        var response = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", Credentials());

        Assert.True(HttpStatusCode.OK == response.StatusCode, await response.Content.ReadAsStringAsync());
        var auth = Assert.Single(pluggy.AuthRequests);
        var expectedBody = $$"""{"clientId":"{{FakePluggyServer.ClientId}}","clientSecret":"{{FakePluggyServer.ClientSecret}}"}""";
        Assert.False(auth.Headers.ContainsKey("Transfer-Encoding"), "POST /auth was sent in chunks");
        Assert.True(auth.Headers.TryGetValue("Content-Length", out var contentLength), "POST /auth has no Content-Length");
        Assert.Equal(expectedBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), contentLength);
        Assert.Equal(expectedBody, auth.Body);
        Assert.Equal("application/json; charset=utf-8", auth.Headers["Content-Type"]);
    }

    [Fact]
    public async Task ARedirectFromPluggy_IsNotFollowed_AndAnswersPluggyUnavailable()
    {
        await using var pluggy = new RedirectingPluggyServer();
        await using var factory = new OpenFinanceApiFactory { LoopbackPluggyAddress = pluggy.Address };
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);

        var response = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });

        await AssertErrorAsync(response, HttpStatusCode.BadGateway, "PLUGGY_UNAVAILABLE",
            "O Pluggy não respondeu agora. Tente de novo em alguns minutos.");
        Assert.Contains($"GET /items/{FakePluggyServer.ItemWithAccounts}", pluggy.Requests); // the 302 was really answered
        Assert.DoesNotContain(pluggy.Requests, request => request.Contains(RedirectingPluggyServer.ElsewherePath, StringComparison.Ordinal));
        Assert.Empty(pluggy.KeysSentElsewhere); // the API key went nowhere else
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_items"));
    }
}
