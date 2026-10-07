namespace CoupleSync.Api.Contracts.OpenFinance;

/// <summary>
/// The requests that carry credentials print nothing of them: MVC writes the arguments of an action to the log
/// (Trace level) through <c>ToString()</c>, and a record would print every property.
/// </summary>
public sealed record TestCredentialsRequest(string ClientId, string ClientSecret)
{
    public override string ToString() => nameof(TestCredentialsRequest);
}

public sealed record TestCredentialsResponse(bool Valid);

/// <summary><c>HistoryMonths</c> is optional (3 when absent); allowed: 3, 6 or 12.</summary>
public sealed record CreateBankConnectionRequest(string Label, string ClientId, string ClientSecret, int? HistoryMonths)
{
    public override string ToString() => nameof(CreateBankConnectionRequest);
}

public sealed record AddBankItemRequest(string ItemId);

/// <summary>Nullable so that a body without the field is a validation error instead of "false".</summary>
public sealed record UpdateBankAccountRequest(bool? SyncEnabled);

/// <summary>No response of these routes carries the client secret nor the whole client id.</summary>
public sealed record OpenFinanceStatusResponse(bool Available, IReadOnlyList<BankConnectionResponse> Connections);

public sealed record BankConnectionResponse(
    Guid Id,
    string Label,
    Guid UserId,
    string UserName,
    bool IsMine,
    string Status,
    string? ClientIdHint,
    int HistoryMonths,
    DateTime? LastSyncAtUtc,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTime CreatedAtUtc,
    IReadOnlyList<BankItemResponse> Items);

public sealed record BankItemResponse(
    Guid Id,
    string ConnectorName,
    string Status,
    string? ExecutionStatus,
    DateTime? LastUpdatedAtUtc,
    string? LastErrorMessage,
    IReadOnlyList<BankAccountResponse> Accounts);

public sealed record BankAccountResponse(
    Guid Id,
    string Type,
    string? Subtype,
    string Name,
    string? MarketingName,
    string? NumberMasked,
    string Currency,
    decimal Balance,
    DateTime BalanceAtUtc,
    decimal? CreditLimit,
    decimal? AvailableCreditLimit,
    DateOnly? BalanceCloseDate,
    DateOnly? BalanceDueDate,
    decimal? MinimumPayment,
    string? Brand,
    bool SyncEnabled);
