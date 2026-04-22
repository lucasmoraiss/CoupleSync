namespace CoupleSync.Api.Contracts.Transactions;

public sealed record CreateManualTransactionRequest(
    decimal Amount,
    string? Currency,
    DateTime? EventTimestampUtc,
    string? Description,
    string? Merchant,
    string Category);
