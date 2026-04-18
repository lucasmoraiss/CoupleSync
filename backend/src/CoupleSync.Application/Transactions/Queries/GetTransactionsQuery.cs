namespace CoupleSync.Application.Transactions.Queries;

public sealed record GetTransactionsQuery(
    Guid CoupleId,
    int Page,
    int PageSize,
    string? Category,
    DateTime? StartDate,
    DateTime? EndDate);
