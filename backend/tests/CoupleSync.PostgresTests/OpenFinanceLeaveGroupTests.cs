using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #31 on real PostgreSQL: who leaves a group (or is removed from it) takes their Open Finance out of it.
/// The deletes respect the foreign keys of bank_accounts, bank_items and bank_connections, happen in the transaction
/// of the exit, and need no encryption key. Pluggy is <see cref="FakePluggyServer"/>; every row is invented.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenFinanceLeaveGroupTests
{
    private const string Base = "/api/v1/openfinance";

    private readonly PostgresServer _server;

    public OpenFinanceLeaveGroupTests(PostgresServer server) => _server = server;

    // ---------------------------------------------------------------- what is deleted, and what is not

    [PostgresFact]
    public async Task Leaving_DeletesTheConnectionItemsAndAccountsOfWhoLeft_AndNothingElse_EvenWithoutTheEncryptionKey()
        => await TheExitDeletesOnlyTheirOpenFinanceInThatGroupAsync(removedByTheOwner: false);

    [PostgresFact]
    public async Task BeingRemovedByTheOwner_DeletesTheConnectionItemsAndAccountsOfTheMember_AndNothingElse_EvenWithoutTheEncryptionKey()
        => await TheExitDeletesOnlyTheirOpenFinanceInThatGroupAsync(removedByTheOwner: true);

    private async Task TheExitDeletesOnlyTheirOpenFinanceInThatGroupAsync(bool removedByTheOwner)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithoutTheEncryptionKey(factory);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var group = ana.CoupleId!.Value;
        var brunosOtherGroup = await CreateAnotherGroupAsync(bruno);

        // Bruno in the group: a connection with two items and three accounts.
        var brunoConnection = await OpenFinanceTests.InsertConnectionAsync(database, group, bruno.UserId);
        var brunoItem1 = await OpenFinanceTests.InsertItemAsync(database, group, brunoConnection, "item-bruno-1");
        var brunoItem2 = await OpenFinanceTests.InsertItemAsync(database, group, brunoConnection, "item-bruno-2");
        await OpenFinanceTests.InsertAccountAsync(database, group, brunoItem1, "account-bruno-1a");
        await OpenFinanceTests.InsertAccountAsync(database, group, brunoItem1, "account-bruno-1b");
        await OpenFinanceTests.InsertAccountAsync(database, group, brunoItem2, "account-bruno-2a");
        // Ana in the same group, and Bruno in another group of his: both must stay.
        var anaConnection = await OpenFinanceTests.InsertConnectionAsync(database, group, ana.UserId);
        var anaItem = await OpenFinanceTests.InsertItemAsync(database, group, anaConnection, "item-ana");
        var anaAccount = await OpenFinanceTests.InsertAccountAsync(database, group, anaItem, "account-ana");
        var brunoOtherConnection = await OpenFinanceTests.InsertConnectionAsync(database, brunosOtherGroup, bruno.UserId);
        var brunoOtherItem = await OpenFinanceTests.InsertItemAsync(database, brunosOtherGroup, brunoOtherConnection, "item-bruno-other");
        var brunoOtherAccount = await OpenFinanceTests.InsertAccountAsync(database, brunosOtherGroup, brunoOtherItem, "account-bruno-other");

        // This server has no encryption key: Open Finance is unavailable, and leaving does not depend on it.
        var before = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.False(before.GetProperty("available").GetBoolean());
        Assert.Equal(2, before.GetProperty("connections").GetArrayLength());

        if (removedByTheOwner)
        {
            var removed = await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}");
            Assert.True(HttpStatusCode.NoContent == removed.StatusCode, await removed.Content.ReadAsStringAsync());
        }
        else
        {
            var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
            Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
            var body = await left.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));
        }

        // Bruno is out of the group, and so is everything of his Open Finance there...
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM couple_members WHERE user_id = @user AND couple_id = @couple", ("user", bruno.UserId), ("couple", group)));
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_connections WHERE user_id = @user AND couple_id = @couple", ("user", bruno.UserId), ("couple", group)));
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_items WHERE connection_id = @connection", ("connection", brunoConnection)));
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_accounts WHERE item_id = @one OR item_id = @two", ("one", brunoItem1), ("two", brunoItem2)));
        // ...while Ana's, and his own in the other group, are exactly the rows that are left.
        Assert.Equal(
            new[] { anaConnection, brunoOtherConnection }.Order(),
            (await database.RowsAsync("SELECT id FROM bank_connections")).Select(r => (Guid)r[0]!).Order());
        Assert.Equal(
            new[] { anaItem, brunoOtherItem }.Order(),
            (await database.RowsAsync("SELECT id FROM bank_items")).Select(r => (Guid)r[0]!).Order());
        Assert.Equal(
            new[] { anaAccount, brunoOtherAccount }.Order(),
            (await database.RowsAsync("SELECT id FROM bank_accounts")).Select(r => (Guid)r[0]!).Order());
        Assert.Equal(1, await database.ScalarAsync<long>(
            "SELECT count(*) FROM couple_members WHERE user_id = @user AND couple_id = @couple", ("user", bruno.UserId), ("couple", brunosOtherGroup)));

        // The group no longer sees anything of who left.
        var after = await ana.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        var only = Assert.Single(after.GetProperty("connections").EnumerateArray());
        Assert.Equal(anaConnection, only.GetProperty("id").GetGuid());
        Assert.True(only.GetProperty("isMine").GetBoolean());
    }

    [PostgresFact]
    public async Task TheLastMemberLeaving_AlsoTakesTheirOpenFinanceOutOfTheGroup()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var group = ana.CoupleId!.Value;
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, group, ana.UserId);
        var item = await OpenFinanceTests.InsertItemAsync(database, group, connection, "item-ana");
        await OpenFinanceTests.InsertAccountAsync(database, group, item, "account-ana");

        var left = await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM couple_members"));
        await AssertNothingStoredAsync(database);
    }

    // ---------------------------------------------------------------- one transaction with the exit

    [PostgresFact]
    public async Task WhenDeletingTheConnectionFails_TheExitFailsWithIt_AndNothingAtAllChanged()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", joinCode: ana.JoinCode);
        var group = ana.CoupleId!.Value;
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, group, bruno.UserId);
        var item = await OpenFinanceTests.InsertItemAsync(database, group, connection, "item-bruno");
        await OpenFinanceTests.InsertAccountAsync(database, group, item, "account-bruno-a");
        await OpenFinanceTests.InsertAccountAsync(database, group, item, "account-bruno-b");
        // The last of the three deletes is refused by the database: the two before it were already done.
        await database.ExecuteAsync(
            """
            CREATE FUNCTION refuse_the_delete() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'refused by the test'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_the_delete BEFORE DELETE ON bank_connections FOR EACH ROW EXECUTE FUNCTION refuse_the_delete();
            """);
        var membersBefore = await database.RowsAsync("SELECT user_id, couple_id, role FROM couple_members ORDER BY user_id");
        var usersBefore = await database.RowsAsync("SELECT id, couple_id FROM users ORDER BY id");
        var tokensBefore = await database.RowsAsync("SELECT id, user_id, token_hash FROM refresh_tokens ORDER BY id");

        var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        var removed = await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}");

        foreach (var response in new[] { left, removed })
        {
            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(HttpStatusCode.InternalServerError == response.StatusCode, raw);
            Assert.Equal("INTERNAL_SERVER_ERROR", JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("code").GetString());
            Assert.DoesNotContain("refused by the test", raw, StringComparison.Ordinal);
        }

        // Neither half happened: Bruno is still in the group, with everything of his Open Finance.
        Assert.Equal(membersBefore, await database.RowsAsync("SELECT user_id, couple_id, role FROM couple_members ORDER BY user_id"));
        Assert.Equal(usersBefore, await database.RowsAsync("SELECT id, couple_id FROM users ORDER BY id"));
        Assert.Equal(tokensBefore, await database.RowsAsync("SELECT id, user_id, token_hash FROM refresh_tokens ORDER BY id"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_connections"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_items"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.GetAsync($"{Base}/status")).StatusCode);

        // Once the database accepts the delete, the same request does both.
        await database.ExecuteAsync("DROP TRIGGER refuse_the_delete ON bank_connections");
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM couple_members"));
        await AssertNothingStoredAsync(database);
    }

    // ---------------------------------------------------------------- leaving in the middle of something else

    [PostgresFact]
    public async Task LeavingWhileAnotherRequestChangesTheCredentials_WaitsForIt_AndStillDeletesEverything()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", joinCode: ana.JoinCode);
        var group = ana.CoupleId!.Value;
        var connection = await OpenFinanceTests.InsertConnectionAsync(database, group, bruno.UserId);
        var item = await OpenFinanceTests.InsertItemAsync(database, group, connection, "item-bruno");
        await OpenFinanceTests.InsertAccountAsync(database, group, item, "account-bruno");

        // Another request stored other credentials (the stored secret is the concurrency token) and has not committed yet.
        await using var other = await database.OpenAsync();
        await using var otherTransaction = await other.BeginTransactionAsync();
        await using (var update = other.CreateCommand())
        {
            update.Transaction = otherTransaction;
            update.CommandText = "UPDATE bank_connections SET client_secret_encrypted = 'another-fake-ciphertext' WHERE id = @id";
            update.Parameters.AddWithValue("id", connection);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var leaving = bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        await WaitUntilARequestWaitsForALockAsync(database);
        Assert.False(leaving.IsCompleted);
        await otherTransaction.CommitAsync();

        var left = await leaving;
        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM couple_members"));
        await AssertNothingStoredAsync(database);
    }

    [PostgresFact]
    public async Task LeavingWhileANewItemIsBeingVerified_TheVerificationGets409_NothingIsWrittenBack_AndTheItemCanGoToAnotherGroup()
        => await LeaveWhileVerifyingAsync(itemAlreadyStored: false);

    [PostgresFact]
    public async Task LeavingWhileAStoredItemIsBeingVerifiedAgain_TheVerificationGets409_NothingIsWrittenBack_AndTheItemCanGoToAnotherGroup()
        => await LeaveWhileVerifyingAsync(itemAlreadyStored: true);

    private async Task LeaveWhileVerifyingAsync(bool itemAlreadyStored)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = OpenFinanceTests.WithOpenFinance(factory, pluggy);
        var ana = await OpenFinanceTests.RegisterAsync(factory, host, "Ana");
        var bruno = await OpenFinanceTests.RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var created = await bruno.Client.PostAsJsonAsync($"{Base}/connections", OpenFinanceTests.NewConnection("Bancos do Bruno"));
        Assert.True(HttpStatusCode.Created == created.StatusCode, await created.Content.ReadAsStringAsync());
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (itemAlreadyStored)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await bruno.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts })).StatusCode);
            Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
        }

        // Pluggy holds its answer about the item until the test lets it go.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pluggy.BeforeAnswer = async request =>
        {
            if (!request.Path.StartsWith("/items/", StringComparison.Ordinal)) return;
            asked.TrySetResult();
            await answerNow.Task;
        };

        var verifying = bruno.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(HttpStatusCode.OK == left.StatusCode, await left.Content.ReadAsStringAsync());
        await AssertNothingStoredAsync(database);
        answerNow.SetResult();

        var answer = await verifying;
        var raw = await answer.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.Conflict == answer.StatusCode, raw);
        var error = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("BANK_CONNECTION_CHANGED", error.GetProperty("code").GetString());
        Assert.Equal("Esta conexão mudou durante a verificação. Verifique de novo.", error.GetProperty("message").GetString());
        await AssertNothingStoredAsync(database);

        // The item is free: in a group of his own, Bruno connects again and the unique index accepts the same Item ID.
        pluggy.BeforeAnswer = null;
        var alone = host.CreateClient();
        alone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await left.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        var newGroup = await alone.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.True(newGroup.IsSuccessStatusCode, await newGroup.Content.ReadAsStringAsync());
        var newGroupBody = await newGroup.Content.ReadFromJsonAsync<JsonElement>();
        alone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newGroupBody.GetProperty("accessToken").GetString());
        var again = await alone.PostAsJsonAsync($"{Base}/connections", OpenFinanceTests.NewConnection("Bancos do Bruno"));
        Assert.True(HttpStatusCode.Created == again.StatusCode, await again.Content.ReadAsStringAsync());
        var newConnectionId = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var added = await alone.PostAsJsonAsync($"{Base}/connections/{newConnectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        Assert.True(HttpStatusCode.OK == added.StatusCode, await added.Content.ReadAsStringAsync());
        Assert.Equal(
            new object?[] { newConnectionId, newGroupBody.GetProperty("coupleId").GetGuid() },
            Assert.Single(await database.RowsAsync("SELECT connection_id, couple_id FROM bank_items")));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task AssertNothingStoredAsync(TestDatabase database)
    {
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_items"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_connections"));
    }

    /// <summary>The same API on a server where OPENFINANCE_ENCRYPTION_KEY was never set.</summary>
    private static DerivedTestHost WithoutTheEncryptionKey(PostgresApiFactory factory)
        => factory.WithTestHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Empty (not absent) so that a value in the machine's environment can never leak into the test.
                ["OPENFINANCE_ENCRYPTION_KEY"] = string.Empty,
            })));

    /// <summary>A second group of the same person. Their client keeps the token of the group it already had.</summary>
    private static async Task<Guid> CreateAnotherGroupAsync(TestUser user)
    {
        var created = await user.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("coupleId").GetGuid();
    }

    /// <summary>Returns once some session of this database is waiting for a lock another one holds.</summary>
    private static async Task WaitUntilARequestWaitsForALockAsync(TestDatabase database)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await database.ScalarAsync<long>(
                   "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "No request waited for a lock within 30 seconds.");
            await Task.Delay(50);
        }
    }
}
