using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.TestSupport;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>What the tests of the synchronisation and of the review (issue #25) share. Everything here is invented data.</summary>
internal static class OpenFinanceSyncKit
{
    public const string Base = "/api/v1/openfinance";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? message = null)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(code, body.GetProperty("code").GetString());
        var text = body.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        if (message is not null) Assert.Equal(message, text);
        return body;
    }

    public static async Task<Guid> ConnectAsync(Member member, string label = "Bancos da Ana", int historyMonths = 3)
    {
        var created = await member.Client.PostAsJsonAsync($"{Base}/connections", new
        {
            label,
            clientId = FakePluggyServer.ClientId,
            clientSecret = FakePluggyServer.ClientSecret,
            historyMonths,
        });
        Assert.True(HttpStatusCode.Created == created.StatusCode, await created.Content.ReadAsStringAsync());
        return (await JsonAsync(created)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> AddItemAsync(Member member, Guid connectionId, string itemId = FakePluggyServer.ItemWithAccounts)
    {
        var added = await member.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId });
        Assert.True(HttpStatusCode.OK == added.StatusCode, await added.Content.ReadAsStringAsync());
        return await JsonAsync(added);
    }

    /// <summary>A connection with the item of the fixture (a checking account and a credit card).</summary>
    public static async Task<Guid> ConnectWithBankAsync(Member member, int historyMonths = 3)
    {
        var connectionId = await ConnectAsync(member, historyMonths: historyMonths);
        await AddItemAsync(member, connectionId);
        return connectionId;
    }

    /// <summary>Puts a run in the queue as the route (or the scheduler) would, straight in the table.</summary>
    public static async Task<Guid> EnqueueAsync(
        OpenFinanceApiFactory factory, Member member, Guid connectionId, bool force = false, bool aiConsent = false, string status = "Pending")
    {
        var id = Guid.NewGuid();
        await factory.ExecuteAsync(
            """
            INSERT INTO sync_runs (id, couple_id, connection_id, status, triggered_by, force_item_update, ai_categorization_consent,
                                   transactions_new, transactions_updated, created_at_utc)
            VALUES (@id, @couple, @connection, @status, 'User', @force, @consent, 0, 0, @now)
            """,
            ("@id", id), ("@couple", member.CoupleId), ("@connection", connectionId), ("@status", status),
            ("@force", force), ("@consent", aiConsent), ("@now", factory.Clock.UtcNow));
        return id;
    }

    /// <summary>Waits until the hosted job finished the run (done or failed) and returns its row.</summary>
    public static async Task<Dictionary<string, object?>> WaitForRunAsync(OpenFinanceApiFactory factory, Guid runId, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        Dictionary<string, object?>? row = null;
        while (DateTime.UtcNow < deadline)
        {
            row = (await factory.RowsAsync($"SELECT * FROM sync_runs WHERE id = '{runId.ToString().ToUpperInvariant()}'")).SingleOrDefault();
            if (row is not null && (string)row["status"]! is "Done" or "Failed") return row;
            await Task.Delay(25);
        }

        Assert.Fail($"The run did not finish in {seconds} s (last status: {row?["status"] ?? "no row"}).");
        return row!;
    }

    /// <summary>Enqueues a run and waits for the hosted job to finish it.</summary>
    public static async Task<Dictionary<string, object?>> SyncAsync(
        OpenFinanceApiFactory factory, Member member, Guid connectionId, bool force = false, bool aiConsent = false)
        => await WaitForRunAsync(factory, await EnqueueAsync(factory, member, connectionId, force, aiConsent));

    public static Task<List<Dictionary<string, object?>>> MirrorAsync(OpenFinanceApiFactory factory)
        => factory.RowsAsync("SELECT * FROM bank_transactions ORDER BY pluggy_transaction_id");

    public static async Task<Dictionary<string, object?>> MirrorRowAsync(OpenFinanceApiFactory factory, string pluggyTransactionId)
        => Assert.Single(await factory.RowsAsync($"SELECT * FROM bank_transactions WHERE pluggy_transaction_id = '{pluggyTransactionId}'"));

    public static string Day(DateTime date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
