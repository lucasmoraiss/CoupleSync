namespace CoupleSync.Application.OpenFinance;

public sealed record CreateBankConnectionInput(string Label, string ClientId, string ClientSecret, int? HistoryMonths)
{
    /// <summary>Carries credentials: prints none of them (a record would print every property).</summary>
    public override string ToString() => nameof(CreateBankConnectionInput);
}

/// <summary>What the group sees of Open Finance. Never carries a secret nor a whole client id.</summary>
public sealed record OpenFinanceStatusDto(bool Available, IReadOnlyList<BankConnectionDto> Connections);

public sealed record BankConnectionDto(
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
    IReadOnlyList<BankItemDto> Items);

public sealed record BankItemDto(
    Guid Id,
    string ConnectorName,
    string Status,
    string? ExecutionStatus,
    DateTime? LastUpdatedAtUtc,
    string? LastErrorMessage,
    IReadOnlyList<BankAccountDto> Accounts);

public sealed record BankAccountDto(
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
