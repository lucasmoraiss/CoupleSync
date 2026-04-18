namespace CoupleSync.Api.Contracts.Transactions;

public sealed record TransactionResponse(
    Guid Id,
    Guid UserId,
    string Bank,
    decimal Amount,
    string Currency,
    DateTime EventTimestampUtc,
    string? Description,
    string? Merchant,
    string Category,
    DateTime CreatedAtUtc);

public sealed record GetTransactionsResponse(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<TransactionResponse> Items);
