namespace CoupleSync.Application.Transactions.Queries;

public sealed record TransactionDto(
    Guid Id,
    Guid CoupleId,
    Guid UserId,
    string Bank,
    decimal Amount,
    string Currency,
    DateTime EventTimestampUtc,
    string? Description,
    string? Merchant,
    string Category,
    DateTime CreatedAtUtc);
