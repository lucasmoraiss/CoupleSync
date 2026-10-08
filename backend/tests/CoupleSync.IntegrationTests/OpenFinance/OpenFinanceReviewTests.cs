using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #25 — the routes: asking for a synchronisation and following it, and the review that turns the expenses of
/// the mirror into transactions of the app. Pluggy is <see cref="FakePluggyServer"/>; every value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceReviewTests
{
    private static OpenFinanceApiFactory NewFactory() => new() { FastSync = true };

    private static string CurrentMonth(OpenFinanceApiFactory factory) => BrazilTime.MonthOf(factory.Clock.UtcNow);

    /// <summary>POST sync and wait, through the routes, until the run ends.</summary>
    private static async Task<JsonElement> SyncByRouteAsync(Member member, Guid connectionId, string query = "")
    {
        var accepted = await member.Client.PostAsync($"{Base}/connections/{connectionId}/sync{query}", null);
        Assert.True(HttpStatusCode.Accepted == accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        var runId = (await JsonAsync(accepted)).GetProperty("id").GetGuid();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var run = await member.Client.GetFromJsonAsync<JsonElement>($"{Base}/sync-runs/{runId}");
            if (run.GetProperty("status").GetString() is "Done" or "Failed") return run;
            Assert.True(DateTime.UtcNow < deadline, "The run did not finish in 30 s.");
            await Task.Delay(25);
        }
    }

    /// <summary>A group with a connection synchronised: 4 expenses (one still pending at the bank) and one entry in the mirror.</summary>
    private static async Task<(Member Ana, Guid ConnectionId)> SyncedAsync(OpenFinanceApiFactory factory)
    {
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        return (ana, connectionId);
    }

    /// <summary>The lines waiting in every month that has some, by Pluggy id.</summary>
    private static async Task<Dictionary<string, JsonElement>> PendingLinesAsync(OpenFinanceApiFactory factory, Member member)
    {
        var first = await member.Client.GetFromJsonAsync<JsonElement>($"{Base}/review");
        var lines = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var month in first.GetProperty("pendingByMonth").EnumerateArray())
        {
            var review = await member.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month.GetProperty("month").GetString()}");
            foreach (var line in review.GetProperty("expenses").EnumerateArray())
            {
                var id = line.GetProperty("id").GetGuid();
                var row = Assert.Single(await factory.RowsAsync($"SELECT pluggy_transaction_id FROM bank_transactions WHERE id = '{id.ToString().ToUpperInvariant()}'"));
                lines[(string)row["pluggy_transaction_id"]!] = line.Clone();
            }
        }

        return lines;
    }

    private static async Task<Guid> LineIdAsync(OpenFinanceApiFactory factory, string pluggyTransactionId)
        => Guid.Parse((string)(await MirrorRowAsync(factory, pluggyTransactionId))["id"]!);

    private static Task<HttpResponseMessage> ConfirmAsync(Member member, object body)
        => member.Client.PostAsJsonAsync($"{Base}/review/confirm", body);

    // ---------------------------------------------------------------- POST connections/{id}/sync, GET sync-runs/{id}

    [Fact]
    public async Task Sync_Answers202WithTheRun_TheJobExecutesIt_AndTheRunCanBeFollowed()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        var accepted = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await JsonAsync(accepted);
        var runId = body.GetProperty("id").GetGuid();
        Assert.Equal(connectionId, body.GetProperty("connectionId").GetGuid());
        Assert.Contains(body.GetProperty("status").GetString(), new[] { "Pending", "Running", "Done" });
        Assert.Equal("User", body.GetProperty("triggeredBy").GetString());

        var stored = await WaitForRunAsync(factory, runId);
        Assert.Equal("Done", stored["status"]);
        Assert.Equal("User", stored["triggered_by"]);
        Assert.Equal(0L, stored["force_item_update"]);
        Assert.Equal(0L, stored["ai_categorization_consent"]);

        var run = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/sync-runs/{runId}");
        Assert.Equal("Done", run.GetProperty("status").GetString());
        Assert.Equal(5, run.GetProperty("transactionsNew").GetInt32());
        Assert.Equal(0, run.GetProperty("transactionsUpdated").GetInt32());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("errorCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("errorMessage").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("finishedAtUtc").ValueKind);

        // The status of the connection shows the last synchronisation.
        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("connections")[0].GetProperty("lastSyncAtUtc").ValueKind);
    }

    [Fact]
    public async Task Sync_WithForce_AsksPluggyToReadTheBanksAgain_AndCarriesTheAiConsent()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        var run = await SyncByRouteAsync(ana, connectionId, "?force=true&aiCategorizationConsent=true");

        Assert.Equal("Done", run.GetProperty("status").GetString());
        Assert.Equal(1, factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"));
        var stored = Assert.Single(await factory.RowsAsync("SELECT * FROM sync_runs"));
        Assert.Equal(1L, stored["force_item_update"]);
        Assert.Equal(1L, stored["ai_categorization_consent"]);
        Assert.Equal("User", stored["triggered_by"]);
    }

    [Fact]
    public async Task Sync_WhenTheAppIsOpened_IsMarkedAppOpen_AndNeverForces()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);

        var run = await SyncByRouteAsync(ana, connectionId, "?appOpen=true&force=true");

        Assert.Equal("AppOpen", run.GetProperty("triggeredBy").GetString());
        Assert.Equal(0L, Assert.Single(await factory.RowsAsync("SELECT force_item_update FROM sync_runs"))["force_item_update"]);
        Assert.Equal(0, factory.Pluggy.Count("PATCH", $"/items/{FakePluggyServer.ItemWithAccounts}"));
    }

    [Fact]
    public async Task Sync_WithHistoryMonths_SetsThePeriodOfTheConnection_BeforeTheRunReadsPluggy()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);

        // The last step of the wizard: the connection was stored with 3 months, the person chooses 12.
        var run = await SyncByRouteAsync(ana, connectionId, "?historyMonths=12");

        Assert.Equal("Done", run.GetProperty("status").GetString());
        Assert.Equal(12L, Assert.Single(await factory.RowsAsync("SELECT history_months FROM bank_connections"))["history_months"]);
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(factory.Clock.UtcNow));
        Assert.Equal(
            today.AddMonths(-12).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(factory.Pluggy.WindowsAsked(FakePluggyServer.CheckingAccountId)).From);
        Assert.Equal(12, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status")).GetProperty("connections")[0].GetProperty("historyMonths").GetInt32());

        // Without the parameter the period stays; a period that is not 3, 6 or 12 is refused and enqueues nothing.
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        await SyncByRouteAsync(ana, connectionId);
        Assert.Equal(12L, Assert.Single(await factory.RowsAsync("SELECT history_months FROM bank_connections"))["history_months"]);
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var refused = await AssertErrorAsync(
            await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync?historyMonths=5", null), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.Equal("O período deve ser de 3, 6 ou 12 meses.", refused.GetProperty("errors").GetProperty("historyMonths")[0].GetString());
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM sync_runs")).Count);
    }

    [Fact]
    public async Task Sync_WhenTheConnectionIsConnectedAgainWhileThePeriodIsBeingWritten_Answers409Changed_AndEnqueuesNothing()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        // Between the moment the request read the connection and the moment it saves the period and the run, other
        // credentials are stored (the person disconnected and connected again on another phone).
        factory.BeforeNextSave = () => factory.ExecuteAsync(
            "UPDATE bank_connections SET client_secret_encrypted = 'other-credentials' WHERE id = @id", ("@id", connectionId));

        var answer = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync?historyMonths=12", null);

        await AssertErrorAsync(answer, HttpStatusCode.Conflict, "BANK_CONNECTION_CHANGED", "Esta conexão mudou enquanto a sincronização era pedida. Tente de novo.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM sync_runs"));
        var connection = Assert.Single(await factory.RowsAsync("SELECT history_months, client_secret_encrypted FROM bank_connections"));
        Assert.Equal(3L, connection["history_months"]);
        Assert.Equal("other-credentials", connection["client_secret_encrypted"]);
    }

    [Fact]
    public async Task Sync_WhenTheConnectionIsDeletedWhileTheRunIsBeingEnqueued_Answers404_AndEnqueuesNothing()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        // Between the moment the request read the connection and the moment it saves the run, the person leaves the
        // group (which deletes everything of Open Finance that is theirs).
        factory.BeforeNextSave = () => factory.ExecuteAsync(
            "DELETE FROM bank_accounts; DELETE FROM bank_items; DELETE FROM bank_connections;");

        var answer = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null);

        await AssertErrorAsync(answer, HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND", "Conexão bancária não encontrada.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM sync_runs"));
    }

    [Fact]
    public async Task ASecondSync_InLessThan10Minutes_Answers409SyncTooSoon_WithTheTimeOfTheNext()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        Assert.Equal("Done", (await SyncByRouteAsync(ana, connectionId)).GetProperty("status").GetString());
        var createdAt = DateTime.SpecifyKind(
            DateTime.Parse((string)Assert.Single(await factory.RowsAsync("SELECT created_at_utc FROM sync_runs"))["created_at_utc"]!, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Utc);

        factory.Clock.Advance(TimeSpan.FromMinutes(9));
        var again = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync?force=true", null);

        var error = await AssertErrorAsync(again, HttpStatusCode.Conflict, "SYNC_TOO_SOON");
        var next = createdAt.AddMinutes(10);
        Assert.Equal(
            next.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(error.GetProperty("errors").GetProperty("nextSyncAtUtc").EnumerateArray()).GetString());
        var local = BrazilTime.ToLocal(next).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            $"Esta conexão foi sincronizada há pouco. A próxima sincronização pode ser pedida às {local} (horário de Brasília).",
            error.GetProperty("message").GetString());
        Assert.Single(await factory.RowsAsync("SELECT id FROM sync_runs"));

        // After the 10 minutes it is accepted again.
        factory.Clock.Advance(TimeSpan.FromMinutes(1.1));
        Assert.Equal(HttpStatusCode.Accepted, (await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null)).StatusCode);
    }

    [Fact]
    public async Task ASync_WhileAnotherOfTheConnectionIsWaitingOrRunning_Answers409AlreadyRunning_WithoutPromisingAnHour()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        // Pluggy takes its time: the first run is still running when the second is asked for.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pluggy.BeforeAnswer = request => request.Path == "/transactions" ? release.Task : Task.CompletedTask;
        var first = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstId = (await JsonAsync(first)).GetProperty("id").GetGuid();

        var second = await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync?force=true", null);

        var error = await AssertErrorAsync(
            second, HttpStatusCode.Conflict, "SYNC_ALREADY_RUNNING", "Já há uma sincronização em andamento para esta conexão. Aguarde ela terminar.");
        Assert.DoesNotContain("nextSyncAtUtc", error.GetRawText(), StringComparison.Ordinal);
        Assert.Single(await factory.RowsAsync("SELECT id FROM sync_runs"));

        // Once it ended, what holds the next one is the interval, with its hour.
        release.SetResult();
        Assert.Equal("Done", (await WaitForRunAsync(factory, firstId))["status"]);
        await AssertErrorAsync(await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null), HttpStatusCode.Conflict, "SYNC_TOO_SOON");
    }

    [Fact]
    public async Task TheTenMinutes_CountFromAnyRun_AlsoAFailedOneAndOneOfTheScheduler()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.TransactionsStatus = HttpStatusCode.ServiceUnavailable;
        Assert.Equal("Failed", (await SyncAsync(factory, ana, connectionId))["status"]);

        await AssertErrorAsync(await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null), HttpStatusCode.Conflict, "SYNC_TOO_SOON");
    }

    [Fact]
    public async Task Sync_IsOnlyForWhoConnected_AndOnlyInTheGroup()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var carla = await factory.RegisterAsync("Carla");
        var connectionId = await ConnectWithBankAsync(ana);

        await AssertErrorAsync(await bruno.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null),
            HttpStatusCode.Forbidden, "BANK_CONNECTION_FORBIDDEN", "Só quem conectou pode sincronizar esta conexão bancária.");
        await AssertErrorAsync(await carla.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null),
            HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND");
        await AssertErrorAsync(await ana.Client.PostAsync($"{Base}/connections/{Guid.NewGuid()}/sync", null),
            HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM sync_runs"));

        // The run of the group is followed by anyone of the group, and by nobody else.
        var run = await SyncByRouteAsync(ana, connectionId);
        var runId = run.GetProperty("id").GetGuid();
        Assert.Equal("Done", (await bruno.Client.GetFromJsonAsync<JsonElement>($"{Base}/sync-runs/{runId}")).GetProperty("status").GetString());
        await AssertErrorAsync(await carla.Client.GetAsync($"{Base}/sync-runs/{runId}"), HttpStatusCode.NotFound, "SYNC_RUN_NOT_FOUND");
        await AssertErrorAsync(await ana.Client.GetAsync($"{Base}/sync-runs/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "SYNC_RUN_NOT_FOUND");
    }

    [Fact]
    public async Task Sync_OfADisconnectedConnection_Answers409_AndOnAServerWithoutTheKey503()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).EnsureSuccessStatusCode();

        await AssertErrorAsync(await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null),
            HttpStatusCode.Conflict, "BANK_CONNECTION_DISCONNECTED");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM sync_runs"));

        await using var withoutKey = new OpenFinanceApiFactory(encryptionKey: null);
        var bruno = await withoutKey.RegisterAsync("Bruno");
        await AssertErrorAsync(await bruno.Client.PostAsync($"{Base}/connections/{Guid.NewGuid()}/sync", null),
            HttpStatusCode.ServiceUnavailable, "OPENFINANCE_UNAVAILABLE");
    }

    [Fact]
    public async Task Sync_IsLimitedTo5PerMinutePerUser_InItsOwnBudget()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno");
        var unknown = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            await AssertErrorAsync(await ana.Client.PostAsync($"{Base}/connections/{unknown}/sync", null), HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND");

        var limited = await ana.Client.PostAsync($"{Base}/connections/{unknown}/sync", null);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMIT_EXCEEDED");

        // Another user, and the other Open Finance routes of the same user, have their own budgets.
        await AssertErrorAsync(await bruno.Client.PostAsync($"{Base}/connections/{unknown}/sync", null), HttpStatusCode.NotFound, "BANK_CONNECTION_NOT_FOUND");
        var test = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", new { clientId = FakePluggyServer.ClientId, clientSecret = FakePluggyServer.ClientSecret });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.GetAsync($"{Base}/review")).StatusCode);
    }

    [Fact]
    public async Task AddItem_IsLimitedTo5PerMinutePerUser_InItsOwnBudget()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana); // one call of the budget of the credentials

        for (var i = 0; i < 5; i++)
        {
            var added = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
            Assert.True(HttpStatusCode.OK == added.StatusCode, await added.Content.ReadAsStringAsync());
        }

        var pluggyCalls = factory.Pluggy.Requests.Count;
        var limited = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMIT_EXCEEDED");
        Assert.Equal(pluggyCalls, factory.Pluggy.Requests.Count); // refused before anything reached Pluggy
        // The budget of the credentials (test and connect) is another one.
        var test = await ana.Client.PostAsJsonAsync($"{Base}/credentials/test", new { clientId = FakePluggyServer.ClientId, clientSecret = FakePluggyServer.ClientSecret });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
    }

    [Fact]
    public async Task TheNewRoutes_RequireASessionAndAGroup()
    {
        await using var factory = NewFactory();
        var anonymous = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"{Base}/connections/{id}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Base}/sync-runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Base}/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Base}/review/restore", new[] { id })).StatusCode);

        // Who left the group (the token still names it) reads and reviews nothing of it.
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        (await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).EnsureSuccessStatusCode();
        await AssertErrorAsync(await bruno.Client.GetAsync($"{Base}/review"), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        await AssertErrorAsync(await bruno.Client.PostAsJsonAsync($"{Base}/review/confirm", new { discard = new[] { id } }), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        await AssertErrorAsync(await bruno.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { id }), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
        await AssertErrorAsync(await bruno.Client.GetAsync($"{Base}/sync-runs/{id}"), HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
    }

    // ---------------------------------------------------------------- GET review

    [Fact]
    public async Task Review_ListsTheExpensesWaiting_WithWhatTheScreenShows_AndNeverTheRawJson()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);

        // Anyone of the group sees the review.
        var lines = await PendingLinesAsync(factory, bruno);

        Assert.Equal(4, lines.Count); // the entry (salary) is not an expense
        Assert.DoesNotContain(FakePluggyServer.SalaryTransactionId, lines.Keys);

        var restaurant = lines[FakePluggyServer.RestaurantTransactionId];
        Assert.Equal(58.90m, restaurant.GetProperty("amount").GetDecimal()); // the value of the expense, positive
        Assert.Equal("BRL", restaurant.GetProperty("currency").GetString());
        Assert.Equal("Cantina Exemplo Ltda", restaurant.GetProperty("merchant").GetString());
        Assert.Equal("Cantina Exemplo", restaurant.GetProperty("description").GetString());
        Assert.Equal("ALIMENTACAO", restaurant.GetProperty("suggestedCategory").GetString());
        Assert.Equal("Posted", restaurant.GetProperty("bankStatus").GetString());
        Assert.Equal("Banco Exemplo", restaurant.GetProperty("bankName").GetString());
        Assert.Equal("Conta Corrente", restaurant.GetProperty("accountName").GetString());
        Assert.Equal(
            Day(BrazilTime.ToLocal(DateTime.UtcNow.Date.AddDays(-2).AddHours(15))),
            restaurant.GetProperty("day").GetString());

        var purchase = lines[FakePluggyServer.CardPurchaseTransactionId];
        Assert.Equal(300.00m, purchase.GetProperty("amount").GetDecimal());
        Assert.Equal((2, 6), (purchase.GetProperty("installmentNumber").GetInt32(), purchase.GetProperty("installmentTotal").GetInt32()));
        Assert.Equal("COMPRAS", purchase.GetProperty("suggestedCategory").GetString());
        Assert.Equal("Cartão Exemplo Platinum", purchase.GetProperty("accountName").GetString());
        Assert.Equal("Pending", lines[FakePluggyServer.CardPendingTransactionId].GetProperty("bankStatus").GetString());
        Assert.Equal(JsonValueKind.Null, lines[FakePluggyServer.RideTransactionId].GetProperty("merchant").ValueKind);

        var raw = await (await bruno.Client.GetAsync($"{Base}/review")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakePluggyServer.RawOnlyMarker, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("rawJson", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakePluggyServer.MerchantCnpj, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_IsByMonthOfBrazil_WithTheTotalsInReais_AndTheMonthsThatHaveSomethingWaiting()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 12);
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(factory.Clock.UtcNow));
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        // 01:30 UTC of the first day of this month is still the last day of the month before in Brasília.
        var edge = thisMonth.ToDateTime(new TimeOnly(1, 30), DateTimeKind.Utc);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c1", edge, -10.00m) { Description = "Virada do mes" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c2", lastMonth.AddDays(9).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), -20.50m) { Description = "Mes passado" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c3", lastMonth.AddDays(10).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), -99.00m) { Description = "Em dolar", CurrencyCode = "USD" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c4", thisMonth.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc), -7.25m) { Description = "Primeiro instante do mes" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000c5", lastMonth.AddDays(11).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), 500m) { Type = "CREDIT", Description = "Entrada" },
        ];
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var monthKey = $"{thisMonth.Year:D4}-{thisMonth.Month:D2}";
        var lastKey = $"{lastMonth.Year:D4}-{lastMonth.Month:D2}";

        var previous = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={lastKey}");

        Assert.Equal(lastKey, previous.GetProperty("month").GetString());
        Assert.Equal(
            new[] { "Virada do mes", "Em dolar", "Mes passado" }, // newest day first
            previous.GetProperty("expenses").EnumerateArray().Select(l => l.GetProperty("description").GetString()));
        Assert.Equal(Day(lastMonth.AddMonths(1).AddDays(-1).ToDateTime(TimeOnly.MinValue)), previous.GetProperty("expenses")[0].GetProperty("day").GetString());
        // The purchase in dollars is listed with its currency and stays out of the total in reais.
        Assert.Equal("USD", previous.GetProperty("expenses")[1].GetProperty("currency").GetString());
        Assert.Equal(30.50m, previous.GetProperty("pendingTotalBrl").GetDecimal());
        Assert.Equal(0, previous.GetProperty("discarded").GetArrayLength());

        var current = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={monthKey}");
        Assert.Equal("Primeiro instante do mes", Assert.Single(current.GetProperty("expenses").EnumerateArray()).GetProperty("description").GetString());
        Assert.Equal(7.25m, current.GetProperty("pendingTotalBrl").GetDecimal());

        // Every month counts what is waiting in all of them; without "month" the answer is the current month.
        foreach (var review in new[] { previous, current, await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review") })
        {
            Assert.Equal(4, review.GetProperty("pendingAllMonths").GetInt32());
            Assert.Equal(
                new[] { (monthKey, 1), (lastKey, 3) },
                review.GetProperty("pendingByMonth").EnumerateArray().Select(m => (m.GetProperty("month").GetString()!, m.GetProperty("pending").GetInt32())));
        }

        Assert.Equal(CurrentMonth(factory), (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review")).GetProperty("month").GetString());
        var empty = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month=2020-01");
        Assert.Equal(0, empty.GetProperty("expenses").GetArrayLength());
        Assert.Equal(0m, empty.GetProperty("pendingTotalBrl").GetDecimal());
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("outubro")]
    [InlineData("26-10")]
    [InlineData("2026/10")]
    public async Task Review_WithAMonthThatIsNotAAAAMM_Answers400InPortuguese(string month)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");

        await AssertErrorAsync(await ana.Client.GetAsync($"{Base}/review?month={Uri.EscapeDataString(month)}"),
            HttpStatusCode.BadRequest, "INVALID_MONTH", "O mês deve estar no formato AAAA-MM.");
    }

    [Fact]
    public async Task Review_OfAGroupWithoutOpenFinance_IsEmpty_AndAnotherGroupSeesNothingOfThisOne()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var carla = await factory.RegisterAsync("Carla");

        var review = await carla.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={CurrentMonth(factory)}");

        Assert.Equal(0, review.GetProperty("expenses").GetArrayLength());
        Assert.Equal(0, review.GetProperty("discarded").GetArrayLength());
        Assert.Equal(0, review.GetProperty("pendingAllMonths").GetInt32());
        Assert.Equal(0, review.GetProperty("pendingByMonth").GetArrayLength());
        Assert.Equal(4, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review")).GetProperty("pendingAllMonths").GetInt32());
    }

    // ---------------------------------------------------------------- POST review/confirm

    [Fact]
    public async Task Confirm_TurnsTheExpensesIntoTransactionsOfWhoConnected_WithTheBankTheMerchantTheDateAndTheCategory()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var ride = await LineIdAsync(factory, FakePluggyServer.RideTransactionId);
        var purchase = await LineIdAsync(factory, FakePluggyServer.CardPurchaseTransactionId);

        // Anyone of the group confirms: here the partner, choosing a category for one line and a description for another.
        var confirmed = await ConfirmAsync(bruno, new
        {
            expenses = new object[]
            {
                new { id = restaurant },
                new { id = ride, category = "lazer", description = "  Corrida para o cinema  " },
                new { id = purchase, category = "" },
            },
        });

        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        var body = await JsonAsync(confirmed);
        Assert.Equal(3, body.GetProperty("created").GetArrayLength());
        Assert.Equal(0, body.GetProperty("discarded").GetArrayLength());
        Assert.Equal(0, body.GetProperty("alreadyConfirmed").GetInt32());
        Assert.Equal(new[] { restaurant, ride, purchase }, body.GetProperty("created").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));

        var transactions = (await factory.RowsAsync("SELECT * FROM transactions")).ToDictionary(t => (string)t["id"]!);
        Assert.Equal(3, transactions.Count);
        Assert.All(transactions.Values, t =>
        {
            Assert.Equal(3L, t["source"]); // OpenFinance
            Assert.Equal("Banco Exemplo", t["bank"]);
            Assert.Equal("BRL", t["currency"]);
            Assert.Equal(ana.UserId.ToString().ToUpperInvariant(), t["user_id"]); // of who connected, not of who confirmed
            Assert.Equal(ana.CoupleId.ToString().ToUpperInvariant(), t["couple_id"]);
        });

        async Task<Dictionary<string, object?>> TransactionOfAsync(string pluggyId)
        {
            var line = await MirrorRowAsync(factory, pluggyId);
            Assert.Equal("Confirmed", line["review_state"]);
            Assert.NotNull(line["reviewed_at_utc"]);
            return transactions[(string)line["linked_transaction_id"]!];
        }

        var first = await TransactionOfAsync(FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("58.9", Convert.ToString(first["amount"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Cantina Exemplo Ltda", first["merchant"]);
        Assert.Equal("Cantina Exemplo", first["description"]);
        Assert.Equal("ALIMENTACAO", first["category"]); // the suggestion
        Assert.Equal((await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId))["date"], first["event_timestamp_utc"]);

        var second = await TransactionOfAsync(FakePluggyServer.RideTransactionId);
        Assert.Equal("LAZER", second["category"]); // the one chosen
        Assert.Equal("Corrida para o cinema", second["description"]);
        Assert.Equal("Corrida Exemplo", second["merchant"]); // no merchant at Pluggy: the description of the bank

        var third = await TransactionOfAsync(FakePluggyServer.CardPurchaseTransactionId);
        Assert.Equal("300.0", Convert.ToString(third["amount"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("COMPRAS", third["category"]);

        // One ingest row per transaction, with the bank OPENFINANCE; they do not count as captured notifications.
        var ingests = await factory.RowsAsync("SELECT * FROM transaction_event_ingests");
        Assert.Equal(3, ingests.Count);
        Assert.All(ingests, i => Assert.Equal("OPENFINANCE", i["bank"]));
        Assert.Equal(transactions.Values.Select(t => (string)t["ingest_event_id"]!).Order(), ingests.Select(i => (string)i["id"]!).Order());

        // The app lists them as any other transaction.
        var listed = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions?page=1&pageSize=50");
        Assert.Equal(3, listed.GetProperty("totalCount").GetInt32());

        // What is left in the review: the line still pending at the bank.
        Assert.Equal(FakePluggyServer.CardPendingTransactionId, Assert.Single(await PendingLinesAsync(factory, ana)).Key);
    }

    [Fact]
    public async Task ConfirmingTheSameLineTwice_CreatesASingleTransaction()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var body = new { expenses = new[] { new { id = restaurant } } };

        var first = await ConfirmAsync(ana, body);
        var second = await ConfirmAsync(ana, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(HttpStatusCode.OK == second.StatusCode, await second.Content.ReadAsStringAsync());
        var again = await JsonAsync(second);
        Assert.Equal(0, again.GetProperty("created").GetArrayLength());
        Assert.Equal(1, again.GetProperty("alreadyConfirmed").GetInt32());
        var transaction = Assert.Single(await factory.RowsAsync("SELECT id, fingerprint FROM transactions"));
        Assert.Single(await factory.RowsAsync("SELECT id FROM transaction_event_ingests"));
        Assert.Equal(transaction["id"], (await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId))["linked_transaction_id"]);
    }

    [Fact]
    public async Task ConfirmingALineWhoseTransactionAlreadyExists_AddsNothing_AndPointsTheLineToIt()
    {
        // The fingerprint of the transaction is the identity of the bank transaction in the group: if the line comes
        // back to the review while its transaction is still there, confirming it again can never store a second one.
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        (await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } } })).EnsureSuccessStatusCode();
        var transaction = Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
        await factory.ExecuteAsync(
            "UPDATE bank_transactions SET review_state = 'Pending', linked_transaction_id = NULL WHERE pluggy_transaction_id = @id",
            ("@id", FakePluggyServer.RestaurantTransactionId));

        var again = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } } });

        Assert.True(HttpStatusCode.OK == again.StatusCode, await again.Content.ReadAsStringAsync());
        Assert.Equal(1, (await JsonAsync(again)).GetProperty("alreadyConfirmed").GetInt32());
        Assert.Equal(transaction["id"], Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"))["id"]);
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal(("Confirmed", transaction["id"]), (line["review_state"], line["linked_transaction_id"]));
    }

    [Fact]
    public async Task TwoGroups_WithTheSamePluggyIdInTheirHistory_NeverShareAFingerprint()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        (await ConfirmAsync(ana, new { expenses = new[] { new { id = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId) } } })).EnsureSuccessStatusCode();
        var fingerprint = (string)Assert.Single(await factory.RowsAsync("SELECT fingerprint FROM transactions"))["fingerprint"]!;

        Assert.Equal(
            CoupleSync.Infrastructure.Security.TransactionFingerprintGenerator.GenerateStatic(
                ana.CoupleId, "OPENFINANCE", 0m, "BRL", DateTime.UnixEpoch, FakePluggyServer.RestaurantTransactionId),
            fingerprint);
        Assert.NotEqual(
            CoupleSync.Infrastructure.Security.TransactionFingerprintGenerator.GenerateStatic(
                Guid.NewGuid(), "OPENFINANCE", 0m, "BRL", DateTime.UnixEpoch, FakePluggyServer.RestaurantTransactionId),
            fingerprint);
    }

    [Fact]
    public async Task ConfirmingOrDiscardingALineOfAnotherGroup_Answers404_AndChangesNothing()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var carla = await factory.RegisterAsync("Carla");
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);

        await AssertErrorAsync(await ConfirmAsync(carla, new { expenses = new[] { new { id = restaurant } } }),
            HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND", "Lançamento do banco não encontrado.");
        await AssertErrorAsync(await ConfirmAsync(carla, new { discard = new[] { restaurant } }),
            HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND");
        await AssertErrorAsync(await carla.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { restaurant }),
            HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND");
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = Guid.NewGuid() } } }),
            HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND");

        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.All(await MirrorAsync(factory), row => Assert.Equal("Pending", row["review_state"]));
    }

    [Fact]
    public async Task ALineStillPendingAtTheBank_CannotBeConfirmed_422_AndNothingOfTheRequestIsStored()
    {
        await using var factory = NewFactory();
        var (ana, connectionId) = await SyncedAsync(factory);
        var pending = await LineIdAsync(factory, FakePluggyServer.CardPendingTransactionId);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var ride = await LineIdAsync(factory, FakePluggyServer.RideTransactionId);

        var refused = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant }, new { id = pending } }, discard = new[] { ride } });

        await AssertErrorAsync(refused, HttpStatusCode.UnprocessableEntity, "TRANSACTION_NOT_POSTED",
            "Este lançamento ainda está pendente no banco. Ele poderá ser confirmado quando o banco o efetivar.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.All(await MirrorAsync(factory), row => Assert.Equal("Pending", row["review_state"]));

        // It can be discarded meanwhile; and once the bank settles it, a new synchronisation lets it be confirmed.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ana, new { discard = new[] { pending } })).StatusCode);
        (await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { pending })).EnsureSuccessStatusCode();
        var card = factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId];
        card[1] = card[1] with { Status = "POSTED" };
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        var confirmed = await ConfirmAsync(ana, new { expenses = new[] { new { id = pending } } });
        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        Assert.Equal("SAUDE", Assert.Single(await factory.RowsAsync("SELECT category FROM transactions"))["category"]);
    }

    [Fact]
    public async Task Discard_TakesTheLineOutOfTheExpenses_AndRestore_BringsItBack()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var ride = await LineIdAsync(factory, FakePluggyServer.RideTransactionId);
        var month = BrazilTime.MonthOf(DateTime.UtcNow.Date.AddDays(-3).AddHours(12));

        var discarded = await ConfirmAsync(bruno, new { discard = new[] { ride, ride } });

        Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
        Assert.Equal(ride, Assert.Single((await JsonAsync(discarded)).GetProperty("discarded").EnumerateArray()).GetGuid());
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        var review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        Assert.DoesNotContain(review.GetProperty("expenses").EnumerateArray(), l => l.GetProperty("id").GetGuid() == ride);
        var line = Assert.Single(review.GetProperty("discarded").EnumerateArray());
        Assert.Equal(ride, line.GetProperty("id").GetGuid());
        Assert.Equal("Corrida Exemplo", line.GetProperty("description").GetString());
        Assert.Equal(3, review.GetProperty("pendingAllMonths").GetInt32());
        // Discarding again changes nothing; confirming a discarded line asks for a refresh.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ana, new { discard = new[] { ride } })).StatusCode);
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = ride } } }), HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING");

        var restored = await bruno.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { ride });

        Assert.True(HttpStatusCode.OK == restored.StatusCode, await restored.Content.ReadAsStringAsync());
        Assert.Equal(ride, Assert.Single((await JsonAsync(restored)).GetProperty("restored").EnumerateArray()).GetGuid());
        var row = await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId);
        Assert.Equal("Pending", row["review_state"]);
        Assert.Null(row["reviewed_at_utc"]);
        review = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        Assert.Equal(0, review.GetProperty("discarded").GetArrayLength());
        Assert.Equal(4, review.GetProperty("pendingAllMonths").GetInt32());
        // Restoring a line that is waiting changes nothing.
        Assert.Equal(0, (await JsonAsync(await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { ride }))).GetProperty("restored").GetArrayLength());
    }

    [Fact]
    public async Task DeletingATransactionThatCameFromTheBank_SendsItsLineBackAsDiscarded_AndItCanBeRestoredAndConfirmedAgain()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var created = await JsonAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } } }));
        var transactionId = created.GetProperty("created")[0].GetProperty("transactionId").GetGuid();
        var fingerprint = Assert.Single(await factory.RowsAsync("SELECT fingerprint FROM transactions"))["fingerprint"];

        // The existing route of transactions.
        var deleted = await ana.Client.DeleteAsync($"/api/v1/transactions/{transactionId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Discarded", line["review_state"]);
        Assert.Null(line["linked_transaction_id"]);
        Assert.NotNull(line["reviewed_at_utc"]);
        // It is not offered as an expense again by itself...
        Assert.DoesNotContain(FakePluggyServer.RestaurantTransactionId, (await PendingLinesAsync(factory, ana)).Keys);

        // ...but it can be restored and confirmed again: a new transaction, with the same fingerprint, and only one.
        (await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { restaurant })).EnsureSuccessStatusCode();
        Assert.Equal("Pending", (await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId))["review_state"]);
        var again = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant, category = "LAZER" } } });

        Assert.True(HttpStatusCode.OK == again.StatusCode, await again.Content.ReadAsStringAsync());
        var newTransactionId = (await JsonAsync(again)).GetProperty("created")[0].GetProperty("transactionId").GetGuid();
        Assert.NotEqual(transactionId, newTransactionId);
        var transaction = Assert.Single(await factory.RowsAsync("SELECT * FROM transactions"));
        Assert.Equal(newTransactionId.ToString().ToUpperInvariant(), transaction["id"]);
        Assert.Equal(fingerprint, transaction["fingerprint"]);
        Assert.Equal("LAZER", transaction["category"]);
        line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal(("Confirmed", newTransactionId.ToString().ToUpperInvariant()), (line["review_state"], line["linked_transaction_id"]));
    }

    [Fact]
    public async Task DeletingAnyOtherTransaction_WorksAsBefore_AndTouchesNoLine()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var created = await ana.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount = 12.34m,
            currency = "BRL",
            description = "Lançamento manual",
            category = "OUTROS",
            eventTimestampUtc = DateTime.UtcNow,
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{id}")).StatusCode);

        Assert.All(await MirrorAsync(factory), row => Assert.Equal("Pending", row["review_state"]));
    }

    [Fact]
    public async Task Confirm_RefusesWhatMakesNoSense_InPortuguese_AndStoresNothing()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var salary = await LineIdAsync(factory, FakePluggyServer.SalaryTransactionId);

        await AssertErrorAsync(await ConfirmAsync(ana, new { }), HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Selecione pelo menos um lançamento.");
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = Array.Empty<object>(), discard = Array.Empty<Guid>() }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION");
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } }, discard = new[] { restaurant } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Um lançamento não pode ser confirmado e descartado ao mesmo tempo.");
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant }, new { id = restaurant } } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "O mesmo lançamento foi enviado mais de uma vez.");
        // An entry is not an expense (entries are a later phase).
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = salary } } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Entradas não são confirmadas como despesa.");
        // A category that is not one of the seven, a description longer than the column, too many lines: 400.
        var badCategory = await AssertErrorAsync(
            await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant, category = "Viagens" } } }), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.Contains("Categoria inválida", badCategory.GetProperty("errors").ToString(), StringComparison.Ordinal);
        await AssertErrorAsync(
            await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant, description = new string('x', 513) } } }), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        await AssertErrorAsync(
            await ConfirmAsync(ana, new { discard = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() }), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        await AssertErrorAsync(await ana.Client.PostAsJsonAsync($"{Base}/review/restore", Array.Empty<Guid>()),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION");

        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.All(await MirrorAsync(factory), row => Assert.Equal("Pending", row["review_state"]));
    }

    [Fact]
    public async Task ALineWithoutValue_DoesNotHoldTheBatchBack_TheOthersAreConfirmed_AndTheAnswerSaysWhichWasSkipped()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        const string zeroId = "c1b2c3d4-0000-4000-8000-0000000000c0";
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId].Add(
            new FakeTransaction(zeroId, DateTime.UtcNow.Date.AddDays(-2).AddHours(16), 0m) { Description = "Tarifa Exemplo Zerada" });
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var zero = await LineIdAsync(factory, zeroId);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var ride = await LineIdAsync(factory, FakePluggyServer.RideTransactionId);

        // "Selecionar tudo": the line without value goes in the middle of the others.
        var confirmed = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant }, new { id = zero }, new { id = ride } } });

        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        var body = await JsonAsync(confirmed);
        Assert.Equal(new[] { restaurant, ride }, body.GetProperty("created").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { zero }, body.GetProperty("skipped").EnumerateArray().Select(s => s.GetGuid()));
        Assert.Equal(0, body.GetProperty("alreadyConfirmed").GetInt32());
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM transactions")).Count);
        // It stays waiting, without a transaction, and can be discarded.
        var line = await MirrorRowAsync(factory, zeroId);
        Assert.Equal("Pending", line["review_state"]);
        Assert.Null(line["linked_transaction_id"]);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ana, new { discard = new[] { zero } })).StatusCode);
        Assert.Equal("Discarded", (await MirrorRowAsync(factory, zeroId))["review_state"]);

        // A request with nothing else to confirm is not an error either.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { zero })).StatusCode);
        var alone = await ConfirmAsync(ana, new { expenses = new[] { new { id = zero } } });
        Assert.True(HttpStatusCode.OK == alone.StatusCode, await alone.Content.ReadAsStringAsync());
        var aloneBody = await JsonAsync(alone);
        Assert.Equal(0, aloneBody.GetProperty("created").GetArrayLength());
        Assert.Equal(new[] { zero }, aloneBody.GetProperty("skipped").EnumerateArray().Select(s => s.GetGuid()));
        Assert.Equal(2, (await factory.RowsAsync("SELECT id FROM transactions")).Count);
    }

    [Fact]
    public async Task Confirm_RaisesTheBudgetAndLargeTransactionAlerts_ThatEveryOtherWayOfEnteringATransactionRaises_OnceForTheWholeConfirmation()
    {
        await using var factory = NewFactory();
        // The budget is by month of Brazil: the clock of the API is put in the middle of a month, so that "a few
        // minutes ago" is the same month whenever this test runs (also in the first minutes of a month).
        var today = BrazilTime.ToLocal(DateTime.UtcNow);
        factory.Clock.SetNow(BrazilTime.ToUtc(new DateTime(today.Year, today.Month, 15, 12, 0, 0)));
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectWithBankAsync(ana);
        var moment = factory.Clock.UtcNow.AddMinutes(-5);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-000000000a01", moment, -58.90m) { Description = "Cantina Exemplo", Category = "Eating out", CategoryId = "11010000" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-000000000a02", moment.AddSeconds(1), -700m) { Description = "Oficina Exemplo", Category = "Taxi and ride-hailing", CategoryId = "19010000" },
            new FakeTransaction("c1b2c3d4-0000-4000-8000-000000000a03", moment.AddSeconds(2), -900m) { Description = "Passagem Exemplo", Category = "Taxi and ride-hailing", CategoryId = "19010000" },
        ];
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        // A budget of R$ 50 for ALIMENTACAO this month.
        var plan = await ana.Client.PostAsJsonAsync("/api/v1/budgets", new { month = CurrentMonth(factory), grossIncome = 6000m, currency = "BRL" });
        Assert.True(HttpStatusCode.OK == plan.StatusCode, await plan.Content.ReadAsStringAsync());
        var planId = (await JsonAsync(plan)).GetProperty("id").GetGuid();
        var allocations = await ana.Client.PutAsJsonAsync($"/api/v1/budgets/{planId}/allocations",
            new { allocations = new[] { new { category = "ALIMENTACAO", allocatedAmount = 50m, currency = "BRL" } } });
        Assert.True(HttpStatusCode.OK == allocations.StatusCode, await allocations.Content.ReadAsStringAsync());
        // Synchronising raised no alert: nothing is a transaction of the app yet.
        const string alertsSql = "SELECT user_id, alert_type, title, body FROM notification_events WHERE alert_type LIKE 'Budget%' OR alert_type LIKE 'LargeTransaction%'";
        Assert.Empty(await factory.RowsAsync(alertsSql));
        var ids = new List<Guid>();
        foreach (var pluggyId in new[] { "c1b2c3d4-0000-4000-8000-000000000a01", "c1b2c3d4-0000-4000-8000-000000000a02", "c1b2c3d4-0000-4000-8000-000000000a03" })
            ids.Add(await LineIdAsync(factory, pluggyId));

        // The partner confirms the three.
        var confirmed = await ConfirmAsync(bruno, new { expenses = ids.Select(id => new { id }).ToArray() });

        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        var events = await factory.RowsAsync(alertsSql);
        var members = new[] { ana.UserId, bruno.UserId }.Select(id => id.ToString().ToUpperInvariant()).Order().ToList();
        // The budget of the category was passed: one alert for each member of the group.
        var budget = events.Where(e => (string)e["alert_type"]! == $"BudgetExceeded|ALIMENTACAO|{CurrentMonth(factory)}").ToList();
        Assert.Equal(members, budget.Select(e => (string)e["user_id"]!).Order());
        Assert.All(budget, e => Assert.Equal("Orçamento de Alimentação estourado", e["title"]));
        // Two lines above the large-transaction limit: ONE summary for each member, not one per line.
        var large = events.Where(e => (string)e["alert_type"]! == "LargeTransaction").ToList();
        Assert.Equal(members, large.Select(e => (string)e["user_id"]!).Order());
        Assert.All(large, e =>
        {
            Assert.Equal("Transações de valor alto", e["title"]);
            // They came from the review of the bank: the text does not say "importadas do extrato".
            Assert.Equal("2 transações de valor alto foram confirmadas na revisão do banco, somando R$ 1.600,00.", e["body"]);
        });

        Assert.Equal(4, events.Count);

        // A confirmation that creates nothing (the same lines again) raises nothing more.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ana, new { expenses = ids.Select(id => new { id }).ToArray() })).StatusCode);
        Assert.Equal(4, (await factory.RowsAsync(alertsSql)).Count);
    }

    [Fact]
    public async Task WhenTheAlertsCannotBeEvaluated_TheConfirmationStands_AndTheAnswerIsStill200()
    {
        await using var factory = new OpenFinanceApiFactory
        {
            FastSync = true,
            ConfigureServices = services =>
            {
                services.RemoveAll<IAlertPolicyService>();
                services.AddScoped<IAlertPolicyService, BrokenAlertPolicy>();
            },
        };
        var (ana, _) = await SyncedAsync(factory);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);

        var confirmed = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } } });

        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        var created = Assert.Single((await JsonAsync(confirmed)).GetProperty("created").EnumerateArray());
        // The transaction and the line were already stored when the alerts failed: nothing is undone.
        var transaction = Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Equal(created.GetProperty("transactionId").GetGuid().ToString().ToUpperInvariant(), transaction["id"]);
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Confirmed", line["review_state"]);
        Assert.Equal(transaction["id"], line["linked_transaction_id"]);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM notification_events"));
        // The failure is logged by its kind only.
        Assert.Contains(factory.Logs.Lines, l => l.Contains("Alert policy evaluation failed", StringComparison.Ordinal) && l.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains(BrokenAlertPolicy.Detail, StringComparison.Ordinal));
    }

    /// <summary>The evaluation of alerts failing (the tables of notifications out of reach, a defect in a rule).</summary>
    private sealed class BrokenAlertPolicy : IAlertPolicyService
    {
        public const string Detail = "alert-failure-detail";

        public Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
            Guid coupleId, Transaction newTransaction, IReadOnlyList<Transaction> recentTransactions, DateTime nowUtc, CancellationToken ct = default)
            => throw new InvalidOperationException(Detail);

        public Task<IReadOnlyList<NotificationEvent>> EvaluatePostImportAsync(
            Guid coupleId, IReadOnlyList<Transaction> importedTransactions, IReadOnlyList<Transaction> recentTransactions, DateTime nowUtc, CancellationToken ct = default)
            => throw new InvalidOperationException(Detail);

        public Task<IReadOnlyList<NotificationEvent>> EvaluatePostBankReviewAsync(
            Guid coupleId, IReadOnlyList<Transaction> confirmedTransactions, IReadOnlyList<Transaction> recentTransactions, DateTime nowUtc, CancellationToken ct = default)
            => throw new InvalidOperationException(Detail);
    }

    [Fact]
    public async Task ConfirmingALineThatSomeoneDiscardsAtTheSameMoment_Answers409_StoresNoTransaction_AndTheLineStaysDiscarded()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        // Between the moment Ana's confirmation read the line and the moment it saves, Bruno discards it.
        factory.BeforeNextSave = async () =>
        {
            var discarded = await ConfirmAsync(bruno, new { discard = new[] { restaurant } });
            Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
        };

        var confirmed = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } } });

        await AssertErrorAsync(confirmed, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT", "A revisão mudou enquanto era confirmada. Atualize e tente de novo.");
        // Nothing by halves: a discarded line never has a transaction.
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transaction_event_ingests"));
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Discarded", line["review_state"]);
        Assert.Null(line["linked_transaction_id"]);
    }

    [Fact]
    public async Task DiscardingALineThatSomeoneConfirmsAtTheSameMoment_Answers409_AndTheLineKeepsItsTransaction()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        var ride = await LineIdAsync(factory, FakePluggyServer.RideTransactionId);
        // Between the moment Ana's discard read the line and the moment it saves, Bruno confirms it.
        factory.BeforeNextSave = async () =>
        {
            var confirmed = await ConfirmAsync(bruno, new { expenses = new[] { new { id = restaurant } } });
            Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        };

        // Another line goes in the same request: nothing of a request that lost is stored.
        var discarded = await ConfirmAsync(ana, new { discard = new[] { restaurant, ride } });

        await AssertErrorAsync(discarded, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT", "A revisão mudou enquanto era confirmada. Atualize e tente de novo.");
        var transaction = Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Confirmed", line["review_state"]);
        Assert.Equal(transaction["id"], line["linked_transaction_id"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, FakePluggyServer.RideTransactionId))["review_state"]);
    }

    [Fact]
    public async Task RestoringALineThatSomeoneRestoresAndConfirmsAtTheSameMoment_Answers409_AndTheLineKeepsItsTransaction()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(ana, new { discard = new[] { restaurant } })).StatusCode);
        factory.BeforeNextSave = async () =>
        {
            Assert.Equal(HttpStatusCode.OK, (await bruno.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { restaurant })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(bruno, new { expenses = new[] { new { id = restaurant } } })).StatusCode);
        };

        var restored = await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { restaurant });

        await AssertErrorAsync(restored, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT", "A revisão mudou enquanto era alterada. Atualize e tente de novo.");
        var transaction = Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
        var line = await MirrorRowAsync(factory, FakePluggyServer.RestaurantTransactionId);
        Assert.Equal("Confirmed", line["review_state"]);
        Assert.Equal(transaction["id"], line["linked_transaction_id"]);
    }

    [Fact]
    public async Task Confirm_Takes200LinesInOneRequest()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] = Enumerable.Range(1, 200)
            .Select(i => new FakeTransaction($"e1b2c3d4-0000-4000-8000-{i:D12}", day.AddHours(12).AddSeconds(i), -i) { Description = $"Compra {i}" })
            .ToList();
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var ids = (await MirrorAsync(factory)).Select(r => Guid.Parse((string)r["id"]!)).ToList();

        var confirmed = await ConfirmAsync(ana, new { expenses = ids.Select(id => new { id }).ToArray() });

        Assert.True(HttpStatusCode.OK == confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
        Assert.Equal(200, (await JsonAsync(confirmed)).GetProperty("created").GetArrayLength());
        Assert.Equal(200L, Assert.Single(await factory.RowsAsync("SELECT count(*) AS n FROM transactions"))["n"]);
        Assert.Equal(200L, Assert.Single(await factory.RowsAsync("SELECT count(DISTINCT fingerprint) AS n FROM transactions"))["n"]);
        Assert.Equal(0, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review")).GetProperty("pendingAllMonths").GetInt32());
    }

    [Fact]
    public async Task APurchaseInAnotherCurrency_BecomesATransactionInThatCurrency_AndADateWithoutTimeKeepsItsDay()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var day = DateTime.UtcNow.Date.AddDays(-4);
        factory.Pluggy.Transactions[FakePluggyServer.CreditCardAccountId] = [];
        factory.Pluggy.Transactions[FakePluggyServer.CheckingAccountId] =
        [
            new FakeTransaction("c1b2c3d4-0000-4000-8000-0000000000d1", day, -25.00m) { Description = "Loja no exterior", CurrencyCode = "USD" },
        ];
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        (await ConfirmAsync(ana, new { expenses = new[] { new { id = await LineIdAsync(factory, "c1b2c3d4-0000-4000-8000-0000000000d1") } } })).EnsureSuccessStatusCode();

        var transaction = Assert.Single(await factory.RowsAsync("SELECT * FROM transactions"));
        Assert.Equal("USD", transaction["currency"]);
        Assert.Equal("OUTROS", transaction["category"]);
        // Midnight UTC is a date without a time: the transaction is at noon of that day in Brasília (15:00 UTC).
        var at = DateTime.Parse((string)transaction["event_timestamp_utc"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(day.AddHours(15), at);
    }

    [Fact]
    public async Task ConfirmingWhileTheLinesGoAwayWithWhoLeft_Answers409_AndStoresNoTransaction()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", ana.JoinCode);
        var connectionId = await ConnectWithBankAsync(bruno);
        Assert.Equal("Done", (await SyncAsync(factory, bruno, connectionId))["status"]);
        var restaurant = await LineIdAsync(factory, FakePluggyServer.RestaurantTransactionId);
        // Bruno leaves between the moment the confirmation read the line and the moment it saves.
        factory.BeforeNextSave = async () =>
        {
            await factory.ExecuteAsync("DELETE FROM bank_transactions");
        };

        var confirmed = await ConfirmAsync(ana, new { expenses = new[] { new { id = restaurant } } });

        await AssertErrorAsync(confirmed, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT", "A revisão mudou enquanto era confirmada. Atualize e tente de novo.");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transaction_event_ingests"));
    }

    // ---------------------------------------------------------------- nothing that existed changed

    [Fact]
    public async Task TheStatusAndTheConnectionRoutes_AnswerWhatTheyAlwaysDid()
    {
        await using var factory = NewFactory();
        var (ana, connectionId) = await SyncedAsync(factory);

        var status = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");

        var connection = Assert.Single(status.GetProperty("connections").EnumerateArray());
        foreach (var field in new[] { "id", "label", "userId", "userName", "isMine", "status", "clientIdHint", "historyMonths", "lastSyncAtUtc", "lastErrorCode", "lastErrorMessage", "createdAtUtc", "items" })
            Assert.True(connection.TryGetProperty(field, out _), field);
        Assert.Equal("Active", connection.GetProperty("status").GetString());
        var account = connection.GetProperty("items")[0].GetProperty("accounts")[0];
        foreach (var field in new[] { "id", "type", "subtype", "name", "marketingName", "numberMasked", "currency", "balance", "balanceAtUtc", "syncEnabled" })
            Assert.True(account.TryGetProperty(field, out _), field);

        // Disconnecting keeps the mirror (and the review of what was already read).
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        Assert.Equal(5, (await MirrorAsync(factory)).Count);
        Assert.Equal(4, (await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/review")).GetProperty("pendingAllMonths").GetInt32());
    }
}
