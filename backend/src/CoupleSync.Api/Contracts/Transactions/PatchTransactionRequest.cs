namespace CoupleSync.Api.Contracts.Transactions;

/// <summary>Partial edit: only the fields that are sent change. An empty description clears it.</summary>
public sealed record PatchTransactionRequest(
    decimal? Amount,
    string? Description,
    DateTime? EventTimestampUtc,
    string? Category);
