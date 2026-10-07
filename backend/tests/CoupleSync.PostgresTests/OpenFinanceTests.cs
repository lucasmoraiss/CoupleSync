using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Issue #24 on real PostgreSQL: the migration that adds bank_connections, bank_items and bank_accounts (additive,
/// over existing data), their unique indexes and foreign keys, the group filter, and the routes writing the real
/// column types. Pluggy is <see cref="FakePluggyServer"/>; the encryption key is generated per test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenFinanceTests
{
    private const string MigrationBeforeOpenFinance = "20261006221833_AddCoupleMembers";
    private const string Base = "/api/v1/openfinance";

    private static readonly string[] NewTables = ["bank_connections", "bank_items", "bank_accounts"];

    private readonly PostgresServer _server;

    public OpenFinanceTests(PostgresServer server) => _server = server;

    // ---------------------------------------------------------------- the migration

    [PostgresFact]
    public async Task TheMigration_OnlyAddsTheThreeTables_AndKeepsEveryExistingRow()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database, LegacyDataSeed.LastLegacyMigration);
        await new LegacyDataSeed().SeedAsync(database, includeOrphans: false);
        await MigrationTests.MigrateAsync(database, MigrationBeforeOpenFinance);

        foreach (var table in NewTables)
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{table}'"));

        var existingTables = (await database.RowsAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name"))
            .Select(r => (string)r[0]!)
            .Where(t => t != "__EFMigrationsHistory")
            .ToList();
        Assert.Contains("transactions", existingTables);
        Assert.Contains("couple_members", existingTables);
        var countsBefore = new Dictionary<string, long>();
        foreach (var table in existingTables) countsBefore[table] = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");
        Assert.True(countsBefore["transactions"] > 0 && countsBefore["users"] > 0, "the seed should have left rows to protect");
        var transactionSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions");
        var incomeSum = await database.ScalarAsync<decimal>("SELECT sum(amount) FROM income_sources");
        var columnsBefore = await ColumnsOfAsync(database, existingTables);

        await MigrationTests.MigrateAsync(database);

        // Additive: no existing table lost a row or changed a column.
        foreach (var table in existingTables)
            Assert.Equal(countsBefore[table], await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(transactionSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM transactions"));
        Assert.Equal(incomeSum, await database.ScalarAsync<decimal>("SELECT sum(amount) FROM income_sources"));
        Assert.Equal(columnsBefore, await ColumnsOfAsync(database, existingTables));

        // The three tables are there, empty.
        foreach (var table in NewTables)
        {
            Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_name = '{table}'"));
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table}"));
        }

        await using var db = MigrationTests.Context(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheMigration_CreatesTheColumnsWithTheTypesTheDesignAsksFor()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);

        var columns = (await database.RowsAsync(
                "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name IN ('bank_connections','bank_items','bank_accounts')"))
            .ToDictionary(r => $"{r[0]}.{r[1]}", r => $"{r[2]} {((string)r[3]! == "YES" ? "null" : "not null")}");

        Assert.Equal("uuid not null", columns["bank_connections.couple_id"]);
        Assert.Equal("uuid not null", columns["bank_connections.user_id"]);
        Assert.Equal("character varying not null", columns["bank_connections.label"]);
        Assert.Equal("character varying null", columns["bank_connections.client_id_encrypted"]);
        Assert.Equal("character varying null", columns["bank_connections.client_secret_encrypted"]);
        Assert.Equal("character varying null", columns["bank_connections.client_id_hint"]);
        Assert.Equal("character varying not null", columns["bank_connections.status"]);
        Assert.Equal("integer not null", columns["bank_connections.history_months"]);
        Assert.Equal("timestamp with time zone null", columns["bank_connections.last_sync_at_utc"]);
        Assert.Equal("character varying null", columns["bank_connections.last_error_code"]);
        Assert.Equal("character varying null", columns["bank_connections.last_error_message"]);
        Assert.Equal("timestamp with time zone not null", columns["bank_connections.created_at_utc"]);
        Assert.Equal("timestamp with time zone not null", columns["bank_connections.updated_at_utc"]);
        Assert.Equal("character varying not null", columns["bank_connections.provider"]);

        Assert.Equal("uuid not null", columns["bank_items.couple_id"]);
        Assert.Equal("uuid not null", columns["bank_items.connection_id"]);
        Assert.Equal("character varying not null", columns["bank_items.pluggy_item_id"]);
        Assert.Equal("character varying not null", columns["bank_items.connector_name"]);
        Assert.Equal("character varying not null", columns["bank_items.status"]);
        Assert.Equal("character varying null", columns["bank_items.execution_status"]);
        Assert.Equal("timestamp with time zone null", columns["bank_items.last_updated_at_utc"]);

        Assert.Equal("uuid not null", columns["bank_accounts.couple_id"]);
        Assert.Equal("uuid not null", columns["bank_accounts.item_id"]);
        Assert.Equal("character varying not null", columns["bank_accounts.pluggy_account_id"]);
        Assert.Equal("character varying null", columns["bank_accounts.number_masked"]);
        Assert.Equal("numeric not null", columns["bank_accounts.balance"]);
        Assert.Equal("timestamp with time zone not null", columns["bank_accounts.balance_at_utc"]);
        Assert.Equal("numeric null", columns["bank_accounts.credit_limit"]);
        Assert.Equal("numeric null", columns["bank_accounts.available_credit_limit"]);
        Assert.Equal("date null", columns["bank_accounts.balance_close_date"]);
        Assert.Equal("date null", columns["bank_accounts.balance_due_date"]);
        Assert.Equal("numeric null", columns["bank_accounts.minimum_payment"]);
        Assert.Equal("boolean not null", columns["bank_accounts.sync_enabled"]);
    }

    private static async Task<List<string>> ColumnsOfAsync(TestDatabase database, IReadOnlyCollection<string> tables)
        => (await database.RowsAsync(
                "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, column_name"))
            .Where(r => tables.Contains((string)r[0]!))
            .Select(r => $"{r[0]}.{r[1]} {r[2]} {r[3]}")
            .ToList();

    // ---------------------------------------------------------------- unique indexes and foreign keys

    [PostgresFact]
    public async Task TheDatabase_RefusesASecondConnectionOfThePersonInTheGroup_ARepeatedItem_AndARepeatedAccount()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var bruno = await factory.RegisterAsync("Bruno", joinCode: ana.JoinCode);
        var carla = await factory.RegisterAsync("Carla"); // another group
        var unknownGroup = Guid.NewGuid();

        var anaConnection = await InsertConnectionAsync(database, ana.CoupleId!.Value, ana.UserId);
        // Another person of the same group, and another group: both fit.
        var brunoConnection = await InsertConnectionAsync(database, bruno.CoupleId!.Value, bruno.UserId);
        var carlaConnection = await InsertConnectionAsync(database, carla.CoupleId!.Value, carla.UserId);

        // One connection per person and group.
        var duplicateConnection = await Assert.ThrowsAsync<PostgresException>(
            () => InsertConnectionAsync(database, ana.CoupleId.Value, ana.UserId));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateConnection.SqlState);
        Assert.Equal("IX_bank_connections_couple_id_user_id", duplicateConnection.ConstraintName);

        // pluggy_item_id: once in the whole database, whatever the connection or the group.
        var itemId = await InsertItemAsync(database, ana.CoupleId.Value, anaConnection, "item-0001");
        foreach (var (couple, connection) in new[] { (ana.CoupleId.Value, anaConnection), (bruno.CoupleId.Value, brunoConnection), (carla.CoupleId.Value, carlaConnection) })
        {
            var duplicateItem = await Assert.ThrowsAsync<PostgresException>(() => InsertItemAsync(database, couple, connection, "item-0001"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateItem.SqlState);
            Assert.Equal("IX_bank_items_pluggy_item_id", duplicateItem.ConstraintName);
        }

        // pluggy_account_id: once in the whole database.
        var carlaItem = await InsertItemAsync(database, carla.CoupleId.Value, carlaConnection, "item-0002");
        await InsertAccountAsync(database, ana.CoupleId.Value, itemId, "account-0001");
        foreach (var (couple, item) in new[] { (ana.CoupleId.Value, itemId), (carla.CoupleId.Value, carlaItem) })
        {
            var duplicateAccount = await Assert.ThrowsAsync<PostgresException>(() => InsertAccountAsync(database, couple, item, "account-0001"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateAccount.SqlState);
            Assert.Equal("IX_bank_accounts_pluggy_account_id", duplicateAccount.ConstraintName);
        }

        // Foreign keys: nothing points to a group, person, connection or item that does not exist...
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => InsertConnectionAsync(database, unknownGroup, ana.UserId))).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => InsertConnectionAsync(database, carla.CoupleId.Value, Guid.NewGuid()))).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => InsertItemAsync(database, ana.CoupleId.Value, Guid.NewGuid(), "item-0003"))).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => InsertAccountAsync(database, ana.CoupleId.Value, Guid.NewGuid(), "account-0002"))).SqlState);
        // ...and a connection with items, or an item with accounts, cannot be deleted from under them.
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync($"DELETE FROM bank_connections WHERE id = '{anaConnection}'"))).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync($"DELETE FROM bank_items WHERE id = '{itemId}'"))).SqlState);

        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM bank_connections"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_items"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
    }

    // ---------------------------------------------------------------- the group filter

    [PostgresFact]
    public async Task TheGlobalFilter_ShowsEachGroupOnlyItsOwnConnectionsItemsAndAccounts()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var carla = await factory.RegisterAsync("Carla");
        var anaGroup = ana.CoupleId!.Value;
        var carlaGroup = carla.CoupleId!.Value;

        var anaConnection = await InsertConnectionAsync(database, anaGroup, ana.UserId);
        var carlaConnection = await InsertConnectionAsync(database, carlaGroup, carla.UserId);
        var anaItem = await InsertItemAsync(database, anaGroup, anaConnection, "item-ana");
        var carlaItem = await InsertItemAsync(database, carlaGroup, carlaConnection, "item-carla");
        var anaAccount = await InsertAccountAsync(database, anaGroup, anaItem, "account-ana");
        var carlaAccount = await InsertAccountAsync(database, carlaGroup, carlaItem, "account-carla");

        await using (var asAna = ContextFor(database, anaGroup))
        {
            Assert.Equal(anaConnection, (await asAna.BankConnections.SingleAsync()).Id);
            Assert.Equal(anaItem, (await asAna.BankItems.SingleAsync()).Id);
            Assert.Equal(anaAccount, (await asAna.BankAccounts.SingleAsync()).Id);
            // Even asking for the other group's rows by id: not there.
            Assert.Null(await asAna.BankConnections.FirstOrDefaultAsync(c => c.Id == carlaConnection));
            Assert.Null(await asAna.BankItems.FirstOrDefaultAsync(i => i.PluggyItemId == "item-carla"));
            Assert.Null(await asAna.BankAccounts.FirstOrDefaultAsync(a => a.Id == carlaAccount));
        }

        await using (var asCarla = ContextFor(database, carlaGroup))
        {
            Assert.Equal(carlaConnection, (await asCarla.BankConnections.SingleAsync()).Id);
            Assert.Equal(carlaItem, (await asCarla.BankItems.SingleAsync()).Id);
            Assert.Equal(carlaAccount, (await asCarla.BankAccounts.SingleAsync()).Id);
        }

        // Without a request (background work, migrations) there is no filter.
        await using var unfiltered = ContextFor(database, null);
        Assert.Equal(2, await unfiltered.BankConnections.CountAsync());
        Assert.Equal(2, await unfiltered.BankItems.CountAsync());
        Assert.Equal(2, await unfiltered.BankAccounts.CountAsync());
    }

    // ---------------------------------------------------------------- the routes on PostgreSQL

    [PostgresFact]
    public async Task TheRoutes_WriteAndReadTheRealColumnTypes_AndKeepGroupsApart()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinance(factory, pluggy);
        var ana = await RegisterAsync(factory, host, "Ana");
        var bruno = await RegisterAsync(factory, host, "Bruno", ana.JoinCode);
        var carla = await RegisterAsync(factory, host, "Carla");

        var created = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Bancos da Ana"));
        Assert.True(HttpStatusCode.Created == created.StatusCode, await created.Content.ReadAsStringAsync());
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var added = await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        Assert.True(HttpStatusCode.OK == added.StatusCode, await added.Content.ReadAsStringAsync());

        // What PostgreSQL holds: encrypted credentials, masked number, numeric(18,2), date and timestamptz.
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM bank_connections WHERE client_secret_encrypted = @secret OR client_id_encrypted = @id OR position(@secret in client_secret_encrypted) > 0",
            ("secret", FakePluggyServer.ClientSecret), ("id", FakePluggyServer.ClientId)));
        Assert.Equal(FakePluggyServer.ClientId[^4..], await database.ScalarAsync<string>("SELECT client_id_hint FROM bank_connections"));
        Assert.Equal("Active", await database.ScalarAsync<string>("SELECT status FROM bank_connections"));
        var card = Assert.Single(await database.RowsAsync(
            "SELECT number_masked, balance, credit_limit, available_credit_limit, minimum_payment, balance_close_date, balance_due_date, brand, sync_enabled, currency FROM bank_accounts WHERE subtype = 'CREDIT_CARD'"));
        Assert.Equal("5678", card[0]);
        Assert.Equal(987.65m, card[1]);
        Assert.Equal(5000m, card[2]);
        Assert.Equal(4012.35m, card[3]);
        Assert.Equal(148.15m, card[4]);
        Assert.Equal(new DateOnly(2026, 10, 20), DayOf(card[5]));
        Assert.Equal(new DateOnly(2026, 10, 27), DayOf(card[6]));
        Assert.Equal("MASTERCARD", card[7]);
        Assert.Equal(true, card[8]);
        Assert.Equal("BRL", card[9]);
        Assert.Equal("1234", await database.ScalarAsync<string>("SELECT number_masked FROM bank_accounts WHERE subtype = 'CHECKING_ACCOUNT'"));
        Assert.Equal(new DateTime(2026, 10, 6, 9, 30, 15, 123, DateTimeKind.Utc), await database.ScalarAsync<DateTime>("SELECT last_updated_at_utc FROM bank_items"));

        // The partner reads the same through the API; another group reads nothing and changes nothing.
        var forBruno = await bruno.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.True(forBruno.GetProperty("available").GetBoolean());
        var connection = Assert.Single(forBruno.GetProperty("connections").EnumerateArray());
        Assert.False(connection.GetProperty("isMine").GetBoolean());
        var accounts = connection.GetProperty("items")[0].GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(2, accounts.Count);
        var cardAccount = accounts.Single(a => a.GetProperty("subtype").GetString() == "CREDIT_CARD");
        Assert.Equal("2026-10-20", cardAccount.GetProperty("balanceCloseDate").GetString());
        Assert.Equal(987.65m, cardAccount.GetProperty("balance").GetDecimal());
        var accountId = cardAccount.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await bruno.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bruno.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false })).StatusCode);
        var forCarla = await carla.Client.GetFromJsonAsync<JsonElement>($"{Base}/status");
        Assert.Equal(0, forCarla.GetProperty("connections").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await carla.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carla.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false })).StatusCode);

        // The item is taken: the unique index of PostgreSQL answers for another group, as 409.
        var carlaCreated = await carla.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Bancos da Carla"));
        var carlaConnection = (await carlaCreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var taken = await carla.Client.PostAsJsonAsync($"{Base}/connections/{carlaConnection}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("BANK_ITEM_ALREADY_CONNECTED", (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_items"));
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));

        // The owner: sync off, verify again (no duplicate, choice kept), disconnect, connect again.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PatchAsJsonAsync($"{Base}/accounts/{accountId}", new { syncEnabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts })).StatusCode);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
        Assert.False(await database.ScalarAsync<bool>($"SELECT sync_enabled FROM bank_accounts WHERE id = '{accountId}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        var afterDisconnect = Assert.Single(await database.RowsAsync(
            $"SELECT status, client_id_encrypted, client_secret_encrypted, client_id_hint FROM bank_connections WHERE id = '{connectionId}'"));
        Assert.Equal(new object?[] { "Disconnected", null, null, null }, afterDisconnect);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));

        var again = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("De volta"));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(connectionId, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_connections"));
    }

    [PostgresFact]
    public async Task TwoConnectionsOfTheSamePersonAtTheSameTime_OnlyOneIsStored_TheOtherGets409()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithOpenFinance(factory, new FakePluggyServer());
        var ana = await RegisterAsync(factory, host, "Ana");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection($"Tentativa {i}"))));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Single(statuses, s => s == HttpStatusCode.Created);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM bank_connections"));
    }

    // ---------------------------------------------------------------- disconnecting in the middle of something else

    [PostgresFact]
    public async Task DisconnectWhileAnItemIsBeingVerified_PluggyAnswersWithTheAccounts_TheVerificationGets409_AndWritesNothing()
        => await DisconnectWhileVerifyingAsync(pluggyRefuses: false);

    [PostgresFact]
    public async Task DisconnectWhileAnItemIsBeingVerified_PluggyRefusesTheCredentials_TheVerificationGets409_AndWritesNothing()
        => await DisconnectWhileVerifyingAsync(pluggyRefuses: true);

    private async Task DisconnectWhileVerifyingAsync(bool pluggyRefuses)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var pluggy = new FakePluggyServer();
        await using var host = WithOpenFinance(factory, pluggy);
        var ana = await RegisterAsync(factory, host, "Ana");
        var created = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Bancos da Ana"));
        Assert.True(HttpStatusCode.Created == created.StatusCode, await created.Content.ReadAsStringAsync());
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (pluggyRefuses) pluggy.DataStatus = HttpStatusCode.Forbidden;

        // Pluggy holds its answer about the item until the test lets it go.
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pluggy.BeforeAnswer = async request =>
        {
            if (!request.Path.StartsWith("/items/", StringComparison.Ordinal)) return;
            asked.TrySetResult();
            await answerNow.Task;
        };

        var verifying = ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")).StatusCode);
        answerNow.SetResult();

        var answer = await verifying;
        var raw = await answer.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.Conflict == answer.StatusCode, raw);
        var error = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("BANK_CONNECTION_CHANGED", error.GetProperty("code").GetString());
        Assert.Equal("Esta conexão mudou durante a verificação. Verifique de novo.", error.GetProperty("message").GetString());

        var row = Assert.Single(await database.RowsAsync(
            "SELECT status, client_id_encrypted, client_secret_encrypted, client_id_hint, last_error_code, last_error_message FROM bank_connections"));
        Assert.Equal(new object?[] { "Disconnected", null, null, null, null, null }, row);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_items"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));

        // Nothing is stuck: the person connects again and the same item is verified.
        pluggy.BeforeAnswer = null;
        pluggy.DataStatus = null;
        Assert.Equal(HttpStatusCode.Created, (await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("De volta"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ana.Client.PostAsJsonAsync($"{Base}/connections/{connectionId}/items", new { itemId = FakePluggyServer.ItemWithAccounts })).StatusCode);
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM bank_accounts"));
    }

    [PostgresFact]
    public async Task ConnectingAgainSixTimesAtOnce_StoresOneSetOfCredentials_TheOthersGet409_AndDisconnectingAtOnceAlwaysErases()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        await using var host = WithOpenFinance(factory, new FakePluggyServer());
        var ana = await RegisterAsync(factory, host, "Ana");
        var created = await ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection("Bancos da Ana"));
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Six disconnections at the same time: every one answers 204 and the credentials are gone.
        var disconnections = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ana.Client.DeleteAsync($"{Base}/connections/{connectionId}")));
        Assert.All(disconnections, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(new object?[] { "Disconnected", null, null },
            Assert.Single(await database.RowsAsync("SELECT status, client_id_encrypted, client_secret_encrypted FROM bank_connections")));

        // Six reconnections at the same time: the stored secret is a concurrency token, so exactly one is stored.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => ana.Client.PostAsJsonAsync($"{Base}/connections", NewConnection($"Tentativa {i}"))));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Single(statuses, s => s == HttpStatusCode.Created);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict));
        var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<JsonElement>();
        var stored = Assert.Single(await database.RowsAsync("SELECT id, label, status FROM bank_connections"));
        Assert.Equal(new object?[] { connectionId, winner.GetProperty("label").GetString(), "Active" }, stored);
    }

    // ---------------------------------------------------------------- helpers

    private static object NewConnection(string label) => new
    {
        label,
        clientId = FakePluggyServer.ClientId,
        clientSecret = FakePluggyServer.ClientSecret,
        historyMonths = 3,
    };

    /// <summary>The same API with a server key (generated here) and the Pluggy client pointed at the fake.</summary>
    private static DerivedTestHost WithOpenFinance(PostgresApiFactory factory, FakePluggyServer pluggy)
        => factory.WithTestHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENFINANCE_ENCRYPTION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["OpenFinance:PluggyBaseUrl"] = FakePluggyServer.BaseUrl,
                ["RateLimiting:OpenFinance:PermitLimit"] = "10000",
            }));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("Pluggy").ConfigurePrimaryHttpMessageHandler(() => pluggy));
        });

    /// <summary>Registers through the factory and hands back a client of the Open Finance host with the same session.</summary>
    private static async Task<TestUser> RegisterAsync(PostgresApiFactory factory, DerivedTestHost host, string name, string? joinCode = null)
    {
        var user = await factory.RegisterAsync(name, joinCode: joinCode);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;
        return user with { Client = client };
    }

    /// <summary>A <c>date</c> column, whichever CLR type the driver hands back for it.</summary>
    private static DateOnly DayOf(object? value) => value switch
    {
        DateOnly day => day,
        DateTime instant => DateOnly.FromDateTime(instant),
        _ => throw new InvalidOperationException($"Not a date: {value}"),
    };

    private static AppDbContext ContextFor(TestDatabase database, Guid? coupleId)
    {
        database.Server.Guard(database.ConnectionString);
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options,
            new FixedCoupleContext(coupleId));
    }

    private sealed class FixedCoupleContext : ICoupleContext
    {
        public FixedCoupleContext(Guid? coupleId) => CoupleId = coupleId;

        public Guid? CoupleId { get; }
    }

    private static async Task<Guid> InsertConnectionAsync(TestDatabase database, Guid coupleId, Guid userId)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO bank_connections (id, couple_id, user_id, provider, label, client_id_encrypted, client_secret_encrypted, client_id_hint, status, history_months, created_at_utc, updated_at_utc)
            VALUES (@id, @couple, @user, 'PLUGGY', 'Bancos', 'not-a-real-ciphertext', 'not-a-real-ciphertext', '0a1b', 'Active', 3, now(), now())
            """,
            ("id", id), ("couple", coupleId), ("user", userId));
        return id;
    }

    private static async Task<Guid> InsertItemAsync(TestDatabase database, Guid coupleId, Guid connectionId, string pluggyItemId)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO bank_items (id, couple_id, connection_id, pluggy_item_id, connector_name, status, created_at_utc)
            VALUES (@id, @couple, @connection, @pluggy, 'Banco Exemplo', 'UPDATED', now())
            """,
            ("id", id), ("couple", coupleId), ("connection", connectionId), ("pluggy", pluggyItemId));
        return id;
    }

    private static async Task<Guid> InsertAccountAsync(TestDatabase database, Guid coupleId, Guid itemId, string pluggyAccountId)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO bank_accounts (id, couple_id, item_id, pluggy_account_id, type, name, currency, balance, balance_at_utc, sync_enabled, updated_at_utc)
            VALUES (@id, @couple, @item, @pluggy, 'BANK', 'Conta Corrente', 'BRL', 10.00, now(), true, now())
            """,
            ("id", id), ("couple", coupleId), ("item", itemId), ("pluggy", pluggyAccountId));
        return id;
    }
}
