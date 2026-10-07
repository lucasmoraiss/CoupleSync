using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.OpenFinance;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.OpenFinance;

/// <summary>Issue #24 — rules of the Open Finance entities and the closed set of Pluggy errors.</summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static BankConnection NewConnection(int historyMonths = 3, string label = "Bancos")
        => BankConnection.Create(Guid.NewGuid(), Guid.NewGuid(), label, "encrypted-id", "encrypted-secret", "0a1b", historyMonths, Now);

    private static BankAccountSnapshot Snapshot(string? number = "0001/98765-1234", string? currency = "brl", string name = "Conta")
        => new("BANK", "CHECKING_ACCOUNT", name, null, number, currency, 10m, null, null, null, null, null, null);

    [Theory]
    [InlineData("0001/98765-1234", "1234")]
    [InlineData("xxxx xxxx xxxx 5678", "5678")]
    [InlineData("12345-6", "3456")]
    [InlineData("987", "987")]
    [InlineData("  4321  ", "4321")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("--/--", null)]
    [InlineData(null, null)]
    public void MaskNumber_KeepsOnlyTheLastFourLettersOrDigits(string? number, string? expected)
        => Assert.Equal(expected, BankAccount.MaskNumber(number));

    [Fact]
    public void AnAccount_NeverHoldsTheWholeNumber_AndStartsWithSyncOn()
    {
        var account = BankAccount.Create(Guid.NewGuid(), Guid.NewGuid(), " account-1 ", Snapshot(), Now);

        Assert.Equal("1234", account.NumberMasked);
        Assert.Equal("account-1", account.PluggyAccountId);
        Assert.Equal("BRL", account.Currency);
        Assert.True(account.SyncEnabled);
        Assert.Equal(Now, account.BalanceAtUtc);
    }

    [Fact]
    public void RefreshingAnAccount_KeepsThePersonsSyncChoice()
    {
        var account = BankAccount.Create(Guid.NewGuid(), Guid.NewGuid(), "account-1", Snapshot(), Now);
        account.SetSyncEnabled(false, Now);

        account.Refresh(Snapshot(number: "777", currency: null) with { Balance = 99.9m }, Now.AddHours(1));

        Assert.False(account.SyncEnabled);
        Assert.Equal(99.9m, account.Balance);
        Assert.Equal("777", account.NumberMasked);
        Assert.Equal("BRL", account.Currency); // no currency from Pluggy: the default
        Assert.Equal(Now.AddHours(1), account.BalanceAtUtc);
    }

    [Fact]
    public void TextsFromPluggy_AreCutToTheColumnSize_InsteadOfFailingTheWrite()
    {
        var longText = new string('x', 2000);

        var account = BankAccount.Create(Guid.NewGuid(), Guid.NewGuid(), "account-1",
            Snapshot(name: longText, currency: longText) with { MarketingName = longText, Brand = longText, Subtype = longText, Type = longText }, Now);
        var item = BankItem.Create(Guid.NewGuid(), Guid.NewGuid(), "item-1", longText, longText, longText, Now, longText, Now);

        Assert.Equal(BankAccount.MaxNameLength, account.Name.Length);
        Assert.Equal(BankAccount.MaxNameLength, account.MarketingName!.Length);
        Assert.Equal(BankAccount.MaxBrandLength, account.Brand!.Length);
        Assert.Equal(BankAccount.MaxTypeLength, account.Type.Length);
        Assert.Equal(BankAccount.MaxTypeLength, account.Subtype!.Length);
        Assert.Equal(BankAccount.MaxCurrencyLength, account.Currency.Length);
        Assert.Equal(BankItem.MaxConnectorNameLength, item.ConnectorName.Length);
        Assert.Equal(BankItem.MaxStatusLength, item.Status.Length);
        Assert.Equal(BankItem.MaxStatusLength, item.ExecutionStatus!.Length);
        Assert.Equal(BankItem.MaxErrorMessageLength, item.LastErrorMessage!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnItemOrAccountWithoutAPluggyId_IsRefused(string id)
    {
        Assert.Throws<ArgumentException>(() => BankItem.Create(Guid.NewGuid(), Guid.NewGuid(), id, "Banco", "UPDATED", null, null, null, Now));
        Assert.Throws<ArgumentException>(() => BankAccount.Create(Guid.NewGuid(), Guid.NewGuid(), id, Snapshot(), Now));
    }

    [Theory]
    [InlineData("00000000-fake-4000-8000-clientid0a1b", "0a1b")]
    [InlineData("  abcdef  ", "cdef")]
    [InlineData("abc", "abc")]
    public void TheHint_IsTheLastFourCharactersOfTheClientId(string clientId, string expected)
        => Assert.Equal(expected, BankConnection.HintOf(clientId));

    [Fact]
    public void ANewConnection_IsActive_WithItsCredentials()
    {
        var connection = NewConnection(historyMonths: 6, label: "  Bancos da Ana ");

        Assert.Equal(BankConnectionStatus.Active, connection.Status);
        Assert.Equal("PLUGGY", connection.Provider);
        Assert.Equal("Bancos da Ana", connection.Label);
        Assert.Equal(6, connection.HistoryMonths);
        Assert.True(connection.HasCredentials);
        Assert.Null(connection.LastSyncAtUtc);
        Assert.Equal(3, BankConnection.DefaultHistoryMonths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(24)]
    [InlineData(-3)]
    public void HistoryMonths_OtherThan3_6_12_AreRefused(int months)
        => Assert.Throws<ArgumentException>(() => NewConnection(months));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AConnectionWithoutALabel_IsRefused(string label)
        => Assert.Throws<ArgumentException>(() => NewConnection(label: label));

    [Fact]
    public void ALabelLongerThanTheLimit_IsRefused()
        => Assert.Throws<ArgumentException>(() => NewConnection(label: new string('a', BankConnection.MaxLabelLength + 1)));

    [Fact]
    public void Disconnect_ErasesTheCredentialsAndTheHint()
    {
        var connection = NewConnection();
        connection.MarkError(PluggyErrorCodes.InvalidCredentials, "mensagem", Now);

        connection.Disconnect(Now.AddMinutes(1));

        Assert.Equal(BankConnectionStatus.Disconnected, connection.Status);
        Assert.Null(connection.ClientIdEncrypted);
        Assert.Null(connection.ClientSecretEncrypted);
        Assert.Null(connection.ClientIdHint);
        Assert.False(connection.HasCredentials);
        Assert.Null(connection.LastErrorCode);
        Assert.Equal(Now.AddMinutes(1), connection.UpdatedAtUtc);

        // On this same copy, a late answer does not bring the connection back. A request that read the connection
        // BEFORE the disconnection holds another copy, which these guards cannot see: that one is stopped by the
        // database (the stored secret is a concurrency token), proved in OpenFinanceIntegrationTests and in
        // CoupleSync.PostgresTests ("DisconnectWhileAnItemIsBeingVerified...").
        connection.MarkError(PluggyErrorCodes.Unavailable, "mensagem", Now.AddMinutes(2));
        Assert.Equal(BankConnectionStatus.Disconnected, connection.Status);
        connection.MarkWorking(Now.AddMinutes(2));
        Assert.Equal(BankConnectionStatus.Disconnected, connection.Status);
    }

    [Fact]
    public void ConnectingAgain_StoresTheNewCredentials_AndKeepsTheSameConnection()
    {
        var connection = NewConnection();
        var id = connection.Id;
        connection.Disconnect(Now);

        connection.Connect("De volta", "new-id", "new-secret", "9z9z", 12, Now.AddDays(1));

        Assert.Equal(id, connection.Id);
        Assert.Equal(BankConnectionStatus.Active, connection.Status);
        Assert.Equal(("new-id", "new-secret", "9z9z", 12), (connection.ClientIdEncrypted, connection.ClientSecretEncrypted, connection.ClientIdHint, connection.HistoryMonths));
        Assert.Equal(Now, connection.CreatedAtUtc);
    }

    [Fact]
    public void AnError_IsShownUntilTheConnectionWorksAgain_AndTheMessageFitsTheColumn()
    {
        var connection = NewConnection();

        connection.MarkError(PluggyErrorCodes.InvalidCredentials, new string('m', 5000), Now);
        Assert.Equal(BankConnectionStatus.Error, connection.Status);
        Assert.Equal(PluggyErrorCodes.InvalidCredentials, connection.LastErrorCode);
        Assert.Equal(BankConnection.MaxErrorMessageLength, connection.LastErrorMessage!.Length);

        connection.MarkWorking(Now);
        Assert.Equal(BankConnectionStatus.Active, connection.Status);
        Assert.Null(connection.LastErrorCode);
        Assert.Null(connection.LastErrorMessage);
    }

    [Theory]
    [InlineData(PluggyErrorCodes.InvalidCredentials, 422)]
    [InlineData(PluggyErrorCodes.ItemNotFound, 404)]
    [InlineData(PluggyErrorCodes.ItemNeedsAction, 422)]
    [InlineData(PluggyErrorCodes.RateLimited, 429)]
    [InlineData(PluggyErrorCodes.Unavailable, 502)]
    public void EveryPluggyError_HasACode_AStatusThatIsNever401_AndAPortugueseMessage(string code, int status)
    {
        var error = new PluggyException(code);

        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.StatusCode);
        Assert.NotEqual(401, error.StatusCode); // 401 makes the app renew its own session
        Assert.StartsWith("PLUGGY_", code, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Contains("Pluggy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(Invalid|not found|must|cannot|unavailable|The |You )\b", error.Message);
    }

    [Fact]
    public void ThePluggyErrorCodes_AreAClosedSet()
    {
        var codes = typeof(PluggyErrorCodes).GetFields().Select(f => (string)f.GetRawConstantValue()!).OrderBy(c => c, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "PLUGGY_INVALID_CREDENTIALS", "PLUGGY_ITEM_NEEDS_ACTION", "PLUGGY_ITEM_NOT_FOUND", "PLUGGY_RATE_LIMITED", "PLUGGY_UNAVAILABLE" },
            codes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluggyException("PLUGGY_SOMETHING_ELSE"));
    }

    [Fact]
    public void TheInputThatCarriesCredentials_PrintsNoneOfThem()
    {
        var input = new CreateBankConnectionInput("Bancos", "fake-client-id-value", "fake-client-secret-value", 3);

        Assert.DoesNotContain("fake-client", input.ToString(), StringComparison.Ordinal);
    }
}
