namespace CoupleSync.Application.Transactions.Commands;

public sealed record CreateManualTransactionCommand(
    Guid CoupleId,
    Guid UserId,
    decimal Amount,
    string Currency,
    DateTime EventTimestampUtc,
    string? Description,
    string? Merchant,
    string Category);
