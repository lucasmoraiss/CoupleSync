namespace CoupleSync.Application.Transactions.Queries;

public sealed record GetTransactionsResult(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<TransactionDto> Items);
