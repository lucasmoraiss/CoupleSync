using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #77 — the transactions come from GET /v2/transactions (by cursor), a refusal of Pluggy is said as a refusal,
/// nothing is removed from the mirror on a listing that did not come whole, and production gets a log of every call
/// and of every run. Pluggy is <see cref="FakePluggyServer"/> (no network); every value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinancePluggyV2Tests
{
    private const string Checking = FakePluggyServer.CheckingAccountId;
    private const string Card = FakePluggyServer.CreditCardAccountId;
    private const string ExtraCardPurchase = "c1b2c3d4-0000-4000-8000-000000000271";

    private static OpenFinanceApiFactory NewFactory() => new() { FastSync = true };

    private static DateOnly BrazilDay(DateTime utc) => DateOnly.FromDateTime(BrazilTime.ToLocal(utc));

    private static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<List<string>> MirrorIdsAsync(OpenFinanceApiFactory factory)
        => (await MirrorAsync(factory)).Select(r => (string)r["pluggy_transaction_id"]!).ToList();

    // ---------------------------------------------------------------- the whole run, by cursor

    [Theory]
    [InlineData(nameof(FakeNextShape.QueryString))]
    [InlineData(nameof(FakeNextShape.QueryStringWithoutQuestionMark))]
    [InlineData(nameof(FakeNextShape.BareCursor))]
    [InlineData(nameof(FakeNextShape.AbsoluteUrl))]
    [InlineData(nameof(FakeNextShape.RelativeUrl))]
    public async Task ARun_ReadsEveryPageOfEveryAccountByCursor_AndReadingAgainDuplicatesNothing(string shape)
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var today = DateTime.UtcNow.Date;
        factory.Pluggy.Transactions[Checking].AddRange(Enumerable.Range(1, 4).Select(i =>
            new FakeTransaction($"c1b2c3d4-0000-4000-8000-0000000077{i:D2}", today.AddDays(-6).AddHours(i), -10m - i) { Description = $"Compra {i}" }));
        factory.Pluggy.MaxPageSize = 2; // the account has 7 transactions: four pages
        factory.Pluggy.NextShape = Enum.Parse<FakeNextShape>(shape);

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", run["status"]);
        Assert.Equal(9L, run["transactions_new"]);
        Assert.Equal(9, (await MirrorIdsAsync(factory)).Count);
        var pages = factory.Pluggy.Requests.Where(r => r.Path == FakePluggyServer.TransactionsPath && r.Query.Contains(Checking, StringComparison.Ordinal)).ToList();
        Assert.Equal(4, pages.Count);
        // Nothing is asked of the route Pluggy discontinued, and nothing of what the cursor listing refuses.
        Assert.Equal(0, factory.Pluggy.Count("GET", FakePluggyServer.LegacyTransactionsPath));
        Assert.All(pages, p =>
        {
            foreach (var refused in new[] { "page=", "pageSize=", "&from=", "&to=" })
                Assert.DoesNotContain(refused, p.Query, StringComparison.Ordinal);
        });

        var again = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", again["status"]);
        Assert.Equal(0L, again["transactions_new"]);
        Assert.Equal(9L, again["transactions_updated"]);
        var ids = await MirrorIdsAsync(factory);
        Assert.Equal(9, ids.Count);
        Assert.Equal(9, ids.Distinct().Count());
    }

    // ---------------------------------------------------------------- what vanished at the bank: only on a whole listing

    [Theory]
    [InlineData("a cursor that comes back")]
    [InlineData("a next page that cannot be read")]
    [InlineData("a listing that never ends")]
    public async Task AListingThatStoppedBeforeTheEnd_KeepsWhatWasRead_RemovesNothing_FailsTheRun_AndTheNextOneAsksThoseDaysAndTheWholeHistory(string reason)
    {
        const string arrivedMeanwhile = "c1b2c3d4-0000-4000-8000-000000000272";
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var today = DateTime.UtcNow.Date;
        factory.Pluggy.Transactions[Card].Add(new FakeTransaction(ExtraCardPurchase, today.AddDays(-3).AddHours(9), 80m) { Description = "Livraria Exemplo" });
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, FakePluggyServer.CardPendingTransactionId))["status"]);
        Assert.Equal(6, (await MirrorIdsAsync(factory)).Count);

        var syncedBefore = Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"];
        Assert.NotNull(syncedBefore);

        // The pending purchase is not in what Pluggy lists now, a new purchase is, and the listing does not come whole.
        factory.Pluggy.Transactions[Card].RemoveAll(t => t.Id == FakePluggyServer.CardPendingTransactionId);
        factory.Pluggy.Transactions[Card].Insert(0, new FakeTransaction(arrivedMeanwhile, today.AddDays(-2).AddHours(11), 64m) { Description = "Papelaria Exemplo" });
        switch (reason)
        {
            case "a cursor that comes back":
                factory.Pluggy.MaxPageSize = 1;
                factory.Pluggy.RepeatCursor = true;
                break;
            case "a next page that cannot be read":
                factory.Pluggy.RawNextJson = "12345";
                break;
            default:
                factory.Pluggy.NeverEnds = true;
                break;
        }

        var partial = await SyncAsync(factory, ana, connectionId, seconds: 120);

        // Not a success: part of the history did not come. The run says so, in words the person can act on.
        Assert.Equal("Failed", partial["status"]);
        Assert.Equal("SYNC_INCOMPLETE", partial["error_code"]);
        Assert.Equal(
            "O Pluggy não entregou a lista completa das transações desta vez. O que chegou foi guardado. Tente sincronizar de novo. (Etapa: transações; listagem incompleta)",
            partial["error_message"]);
        var kept = await MirrorIdsAsync(factory);
        Assert.Contains(arrivedMeanwhile, kept); // what was read is stored
        Assert.Equal(1L, partial["transactions_new"]);
        Assert.Contains(FakePluggyServer.CardPendingTransactionId, kept); // missing from a listing that is not whole proves nothing
        Assert.Equal(7, kept.Count);
        // The last synchronisation does not move.
        var connection = Assert.Single(await factory.RowsAsync("SELECT status, last_sync_at_utc, last_error_code FROM bank_connections"));
        Assert.Equal(syncedBefore, connection["last_sync_at_utc"]);
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_error_code"]);
        var partialId = Guid.Parse((string)partial["id"]!);
        Assert.Contains(factory.Logs.Lines, l =>
            l.StartsWith("Warning CoupleSync.Application.OpenFinance.SyncConnectionService:", StringComparison.Ordinal)
            && l.Contains("did not come whole", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith(
            $"Information CoupleSync.Application.OpenFinance.SyncConnectionService: Open Finance run {partialId} ended: Failed (SYNC_INCOMPLETE); stage transactions; accounts read 2; ",
            StringComparison.Ordinal));
        // It is a failure for the short pause too: who connected is not held for ten minutes.
        await AssertErrorAsync(
            await ana.Client.PostAsync($"{Base}/connections/{connectionId}/sync", null),
            HttpStatusCode.Conflict,
            "SYNC_TOO_SOON",
            "A última tentativa de sincronizar esta conexão acabou de falhar. Aguarde alguns segundos e tente de novo.");

        // The same absence, on a listing Pluggy says is whole (here over several pages): the line leaves, as before.
        factory.Pluggy.RepeatCursor = false;
        factory.Pluggy.RawNextJson = null;
        factory.Pluggy.NeverEnds = false;
        factory.Pluggy.MaxPageSize = 1;

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        // The run after the partial one asks every account whole: from the start of the history, which covers the
        // days the partial one had asked for.
        var historyFrom = Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-3));
        foreach (var account in new[] { Checking, Card })
        {
            var asked = factory.Pluggy.WindowsAsked(account);
            Assert.Equal(3, asked.Count);
            Assert.Equal(historyFrom, asked[2].From);
            Assert.True(string.CompareOrdinal(asked[2].From, asked[1].From) <= 0, $"{asked[2].From} should not be after {asked[1].From}");
        }

        var left = await MirrorIdsAsync(factory);
        Assert.DoesNotContain(FakePluggyServer.CardPendingTransactionId, left);
        Assert.Equal(6, left.Count);
        Assert.NotEqual(syncedBefore, Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]);
    }

    [Fact]
    public async Task TheFirstRunOfAConnection_ThatCameIncomplete_LeavesItNeverSynchronised_AndTheNextAsksTheWholeHistoryAgain()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        factory.Pluggy.RawNextJson = "{\"cursor\":\"somewhere-else\"}";

        var partial = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", partial["status"]);
        Assert.Equal("SYNC_INCOMPLETE", partial["error_code"]);
        Assert.Equal(5L, partial["transactions_new"]);
        Assert.Equal(5, (await MirrorIdsAsync(factory)).Count);
        Assert.Null(Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]);
        // What the app shows is what the route of the run gives.
        var seen = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/sync-runs/{Guid.Parse((string)partial["id"]!)}");
        Assert.Equal("Failed", seen.GetProperty("status").GetString());
        Assert.Equal((string)partial["error_message"]!, seen.GetProperty("errorMessage").GetString());

        factory.Pluggy.RawNextJson = null;
        var whole = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", whole["status"]);
        Assert.Equal(0L, whole["transactions_new"]);
        var historyFrom = Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-3));
        foreach (var account in new[] { Checking, Card })
            Assert.Equal(new[] { historyFrom, historyFrom }, factory.Pluggy.WindowsAsked(account).Select(w => w.From));
        Assert.NotNull(Assert.Single(await factory.RowsAsync("SELECT last_sync_at_utc FROM bank_connections"))["last_sync_at_utc"]);
    }

    [Fact]
    public async Task TheLastDayAsked_IsReadWhole_AndALineOfThatDayNeitherLeavesNorDoubles_HoweverPluggyCutsIt()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        var utcToday = DateTime.UtcNow.Date;
        const string pendingToday = "c1b2c3d4-0000-4000-8000-0000000077b1";
        const string endOfToday = "c1b2c3d4-0000-4000-8000-0000000077b2";
        const string firstDay = "c1b2c3d4-0000-4000-8000-0000000077b3";
        var firstDayAsked = BrazilDay(factory.Clock.UtcNow).AddMonths(-3);
        factory.Pluggy.Transactions[Card].Add(new FakeTransaction(pendingToday, utcToday.AddMinutes(1), 31m) { Description = "Mercado Exemplo", Status = "PENDING" });
        factory.Pluggy.Transactions[Checking].AddRange(
        [
            new FakeTransaction(endOfToday, utcToday.AddHours(23).AddMinutes(59), -32m) { Description = "Posto Exemplo" },
            new FakeTransaction(firstDay, firstDayAsked.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), -33m) { Description = "Banca Exemplo" },
        ]);

        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);

        // "dateTo" is the last day itself (inclusive): nothing of it is lost, at either end of the window.
        var asked = Assert.Single(factory.Pluggy.WindowsAsked(Checking));
        Assert.Equal((Text(firstDayAsked), Text(DateOnly.FromDateTime(factory.Clock.UtcNow))), (asked.From, asked.To));
        var first = await MirrorIdsAsync(factory);
        Assert.Contains(pendingToday, first);
        Assert.Contains(endOfToday, first);
        Assert.Contains(firstDay, first);
        Assert.Equal(8, first.Count);

        // Pluggy cutting the last day at 00:00 (the other way its documentation can be read) stops listing the pending
        // line of today: that day is not taken as covered, so the line stays.
        factory.Pluggy.DateToIsAnInstant = true;
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, pendingToday))["status"]);
        Assert.Equal(8, (await MirrorIdsAsync(factory)).Count);

        // And read whole again, each line is there once.
        factory.Pluggy.DateToIsAnInstant = false;
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var last = await MirrorIdsAsync(factory);
        Assert.Equal(8, last.Count);
        Assert.Equal(8, last.Distinct().Count());
        Assert.Single(last, id => id == pendingToday);
        Assert.Single(last, id => id == endOfToday);
        Assert.Single(last, id => id == firstDay);
    }

    [Fact]
    public async Task ABankAddedLater_WhoseFirstListingCameIncomplete_IsAskedForItsWholeHistoryAgain_UntilARunEndsWell()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana, historyMonths: 3);
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var historyFrom = Text(BrazilDay(factory.Clock.UtcNow).AddMonths(-3));

        // Another bank is added to the connection that already synchronised. Its account has months of history;
        // Pluggy lists the newest first, and the listing stops after the first page.
        const string other = FakePluggyServer.OtherCheckingAccountId;
        const string old1 = "c1b2c3d4-0000-4000-8000-0000000077c1";
        const string old2 = "c1b2c3d4-0000-4000-8000-0000000077c2";
        var today = DateTime.UtcNow.Date;
        factory.Pluggy.Transactions[other].AddRange(
        [
            new FakeTransaction(old1, today.AddDays(-20).AddHours(10), -40m) { Description = "Oficina Exemplo" },
            new FakeTransaction(old2, today.AddDays(-45).AddHours(10), -41m) { Description = "Escola Exemplo" },
        ]);
        await AddItemAsync(ana, connectionId, FakePluggyServer.OtherItemWithAccounts);
        factory.Pluggy.MaxPageSize = 1;
        factory.Pluggy.RawNextJson = "12345";

        var partial = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("SYNC_INCOMPLETE", partial["error_code"]);
        var afterPartial = await MirrorIdsAsync(factory);
        Assert.Contains(FakePluggyServer.OtherAccountTransactionId, afterPartial); // only the newest came, and it is stored
        Assert.DoesNotContain(old1, afterPartial);
        Assert.DoesNotContain(old2, afterPartial);
        Assert.Equal(historyFrom, Assert.Single(factory.Pluggy.WindowsAsked(other)).From);

        // Pluggy answers whole again. The account now has a transaction of two days ago in the mirror, and still the
        // run asks for its whole history: the days that did not come are asked again.
        factory.Pluggy.MaxPageSize = 500;
        factory.Pluggy.RawNextJson = null;

        var whole = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", whole["status"]);
        Assert.Equal(historyFrom, factory.Pluggy.WindowsAsked(other)[1].From);
        var ids = await MirrorIdsAsync(factory);
        Assert.Contains(old1, ids);
        Assert.Contains(old2, ids);
        Assert.Equal(8, ids.Count); // 5 of the first bank, 3 of the one added
        Assert.Equal(8, ids.Distinct().Count());
        Assert.Equal(2L, whole["transactions_new"]);

        // Once a run ended well, the days asked are the usual ones again.
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        var usual = factory.Pluggy.WindowsAsked(other)[2].From;
        Assert.NotEqual(historyFrom, usual);
        Assert.Equal(Text(BrazilDay(today.AddDays(-2).AddHours(10)).AddDays(-7)), usual);
    }

    [Fact]
    public async Task InOneRun_TheAccountThatCameWholeLosesWhatVanished_TheOneThatDidNotKeepsIt_AndTheRunEndsIncomplete()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        var today = DateTime.UtcNow.Date;
        const string pendingAtChecking = "c1b2c3d4-0000-4000-8000-0000000077d1";
        factory.Pluggy.Transactions[Checking].Add(
            new FakeTransaction(pendingAtChecking, today.AddDays(-1).AddHours(9), -27m) { Description = "Lanchonete Exemplo", Status = "PENDING" });
        Assert.Equal("Done", (await SyncAsync(factory, ana, connectionId))["status"]);
        Assert.Equal(6, (await MirrorIdsAsync(factory)).Count);

        // Both pending lines vanish at Pluggy. The card (one transaction left) fits one page: its listing is whole.
        // The checking account (three left) takes two pages, and the second points back at its own cursor.
        factory.Pluggy.Transactions[Card].RemoveAll(t => t.Id == FakePluggyServer.CardPendingTransactionId);
        factory.Pluggy.Transactions[Checking].RemoveAll(t => t.Id == pendingAtChecking);
        factory.Pluggy.MaxPageSize = 2;
        factory.Pluggy.RepeatCursor = true;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("SYNC_INCOMPLETE", run["error_code"]);
        var ids = await MirrorIdsAsync(factory);
        Assert.DoesNotContain(FakePluggyServer.CardPendingTransactionId, ids); // whole listing: what vanished leaves
        Assert.Contains(pendingAtChecking, ids); // partial listing: nothing leaves
        Assert.Equal(5, ids.Count);
        var runId = Guid.Parse((string)run["id"]!);
        Assert.Contains(factory.Logs.Lines, l => l.Contains($"Open Finance run {runId} ended: Failed (SYNC_INCOMPLETE); stage transactions; accounts read 2; ", StringComparison.Ordinal)
                                                 && l.Contains("incomplete listings 1;", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- a refusal is said as a refusal

    [Fact]
    public async Task WhenPluggyAnswers410_TheRunFailsAsARefusal_WithTheStageTheStatusAndWhatPluggySaid_AndTheLogHasIt()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.TransactionsStatus = HttpStatusCode.Gone;
        factory.Pluggy.TransactionsErrorBody = FakePluggyServer.LegacyTransactionsGoneBody;

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("PLUGGY_REFUSED", run["error_code"]);
        Assert.Equal(
            "O Pluggy recusou este pedido. Tentar de novo agora não deve resolver."
            + " (Etapa: transações; resposta do Pluggy: HTTP 410, ENDPOINT_DEPRECATED)",
            run["error_message"]);
        // What the app shows is what the route of the run gives.
        var seen = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/sync-runs/{Guid.Parse((string)run["id"]!)}");
        Assert.Equal("PLUGGY_REFUSED", seen.GetProperty("errorCode").GetString());
        Assert.Equal((string)run["error_message"]!, seen.GetProperty("errorMessage").GetString());
        // The route Pluggy discontinued is not tried as a way out: there is no fallback to it.
        Assert.Equal(0, factory.Pluggy.Count("GET", FakePluggyServer.LegacyTransactionsPath));
        // Nothing wrong with the credentials nor with the bank: the connection and its item are as they were.
        var connection = Assert.Single(await factory.RowsAsync("SELECT status, last_error_code FROM bank_connections"));
        Assert.Equal("Active", connection["status"]);
        Assert.Null(connection["last_error_code"]);

        var runId = Guid.Parse((string)run["id"]!);
        Assert.Contains(factory.Logs.Lines, l => l ==
            $"Warning CoupleSync.Application.OpenFinance.SyncConnectionService: Open Finance run {runId} failed at stage transactions with PLUGGY_REFUSED; Pluggy call transactions answered 410 (410/ENDPOINT_DEPRECATED). ");
        Assert.Contains(factory.Logs.Lines, l =>
            l.StartsWith($"Information CoupleSync.Application.OpenFinance.SyncConnectionService: Open Finance run {runId} ended: Failed (PLUGGY_REFUSED); stage transactions; accounts read 0; transactions new 0, updated 0; incomplete listings 0; took ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A400OfTheListing_IsARefusalOfTheRequest_NeverAnItemIdThatWasNotFound()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.TransactionsStatus = HttpStatusCode.BadRequest;
        factory.Pluggy.TransactionsErrorBody = """{"code":400,"codeDescription":"INVALID_QUERY_PARAMETERS","message":"Unknown query parameter (fake)."}""";

        var run = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Failed", run["status"]);
        Assert.Equal("PLUGGY_REFUSED", run["error_code"]);
        Assert.EndsWith("(Etapa: transações; resposta do Pluggy: HTTP 400, INVALID_QUERY_PARAMETERS)", (string)run["error_message"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("Item ID", (string)run["error_message"]!, StringComparison.Ordinal);
        // The bank is not marked as one Pluggy does not know.
        var item = Assert.Single(await factory.RowsAsync("SELECT * FROM bank_items"));
        Assert.DoesNotContain(item.Values, value => value is string text && text.Contains("Item ID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenPluggyDoesNotAnswer_TheRunSaysSo_WithTheStage()
    {
        await using var factory = NewFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.TransactionsStatus = HttpStatusCode.ServiceUnavailable;
        factory.Pluggy.TransactionsErrorBody = "<html>503 Service Unavailable</html>"; // a gateway, not Pluggy's error body

        var answered = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("PLUGGY_UNAVAILABLE", answered["error_code"]);
        Assert.Equal(
            "O Pluggy não respondeu agora. Tente de novo em alguns minutos. (Etapa: transações; resposta do Pluggy: HTTP 503)",
            answered["error_message"]);

        factory.Pluggy.TransactionsStatus = null;
        factory.Pluggy.BeforeAnswer = request => request.Path == FakePluggyServer.TransactionsPath
            ? Task.FromException(new HttpRequestException("Connection refused (fake)."))
            : Task.CompletedTask;

        var silent = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("PLUGGY_UNAVAILABLE", silent["error_code"]);
        Assert.Equal(
            "O Pluggy não respondeu agora. Tente de novo em alguns minutos. (Etapa: transações; sem resposta válida do Pluggy)",
            silent["error_message"]);
    }

    [Fact]
    public async Task ARefusalOfPluggy_ReachesTheApp_InTheSingleErrorFormat_InPortuguese()
    {
        await using var factory = new OpenFinanceApiFactory();
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectAsync(ana);
        factory.Pluggy.DataStatus = HttpStatusCode.Gone;

        var answer = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });

        // The single format: code, message (Portuguese), errors only for validation, and the trace id.
        var error = await AssertErrorAsync(
            answer,
            HttpStatusCode.BadGateway,
            PluggyErrorCodes.Refused,
            "O Pluggy recusou este pedido. Tentar de novo agora não deve resolver.");
        Assert.Subset(
            new HashSet<string> { "code", "message", "errors", "traceId" },
            error.EnumerateObject().Select(p => p.Name).ToHashSet());
    }

    // ---------------------------------------------------------------- the log production gets

    [Fact]
    public async Task WithTheLogLevelsOfProduction_EveryCallAndEveryRunIsLogged_WithoutSecretsNorBankData()
    {
        await using var factory = new OpenFinanceApiFactory { FastSync = true, ProductionLogLevels = true };
        var ana = await factory.RegisterAsync("Ana");
        var connectionId = await ConnectWithBankAsync(ana);
        factory.Pluggy.MaxPageSize = 2; // the checking account has 3 transactions: two pages, one cursor

        var done = await SyncAsync(factory, ana, connectionId);

        Assert.Equal("Done", done["status"]);
        var doneId = Guid.Parse((string)done["id"]!);
        const string client = "Information CoupleSync.Infrastructure.Integrations.Pluggy.PluggyHttpClient: ";
        const string service = "CoupleSync.Application.OpenFinance.SyncConnectionService: ";
        var lines = factory.Logs.Lines;
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^" + client + @"Pluggy item call answered 200 in \d+ ms; page -; items -; next cursor absent\. $"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^" + client + @"Pluggy accounts call answered 200 in \d+ ms; page -; items 2; next cursor absent\. $"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^" + client + @"Pluggy transactions call answered 200 in \d+ ms; page 1; items 2; next cursor present\. $"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^" + client + @"Pluggy transactions call answered 200 in \d+ ms; page 2; items 1; next cursor absent\. $"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^" + client + @"Pluggy bills call answered 200 in \d+ ms; page 1; items 0; next cursor absent\. $"));
        Assert.Single(lines, l => System.Text.RegularExpressions.Regex.IsMatch(
            l,
            "^Information " + service + $"Open Finance run {doneId} ended: Done; stage finish; accounts read 2; transactions new 5, updated 0; incomplete listings 0; took " + @"\d+ ms\. $"));

        // A run that fails: one warning with where it stopped and what Pluggy answered.
        factory.Pluggy.TransactionsStatus = HttpStatusCode.Gone;
        factory.Pluggy.TransactionsErrorBody = FakePluggyServer.LegacyTransactionsGoneBody;
        var failed = await SyncAsync(factory, ana, connectionId);
        var failedId = Guid.Parse((string)failed["id"]!);

        lines = factory.Logs.Lines;
        Assert.Single(lines, l => l ==
            "Warning " + service + $"Open Finance run {failedId} failed at stage transactions with PLUGGY_REFUSED; Pluggy call transactions answered 410 (410/ENDPOINT_DEPRECATED). ");
        Assert.Single(lines, l => l.StartsWith(
            "Information " + service + $"Open Finance run {failedId} ended: Failed (PLUGGY_REFUSED); stage transactions; ", StringComparison.Ordinal));

        foreach (var forbidden in new[]
                 {
                     FakePluggyServer.ClientId, FakePluggyServer.ClientSecret, "fake-api-key-",
                     FakePluggyServer.ItemWithAccounts, Checking, Card, FakePluggyServer.CheckingAccountNumber, FakePluggyServer.CreditCardNumber,
                     "cur+/", "cur%2B", FakePluggyServer.BaseUrl,
                     "Cantina Exemplo", "Corrida Exemplo", "COMPRA DEBITO", "Pagamento Empresa Exemplo", "Loja Exemplo", "Farmacia Exemplo",
                     FakePluggyServer.MerchantCnpj, FakePluggyServer.RawOnlyMarker, "58.90", "1175.66", "3500.00", "1234.56",
                     "This endpoint is deprecated", "errorid",
                 })
        {
            Assert.DoesNotContain(lines, l => l.Contains(forbidden, StringComparison.Ordinal));
        }
    }
}
