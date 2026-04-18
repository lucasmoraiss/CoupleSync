using CoupleSync.Application.Transactions.Queries;

namespace CoupleSync.Application.Transactions.Commands;

public sealed record UpdateTransactionCategoryResult(
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
