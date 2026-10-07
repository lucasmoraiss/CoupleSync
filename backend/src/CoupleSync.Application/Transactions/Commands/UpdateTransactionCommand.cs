namespace CoupleSync.Application.Transactions.Commands;

/// <summary>Partial edit of a transaction: null fields stay as they are; an empty description or merchant clears it.</summary>
public sealed record UpdateTransactionCommand(
    Guid TransactionId,
    Guid CoupleId,
    decimal? Amount,
    string? Description,
    DateTime? EventTimestampUtc,
    string? Category,
    string? Merchant = null);
