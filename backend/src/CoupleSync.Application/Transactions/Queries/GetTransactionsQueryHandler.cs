using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Transactions.Queries;

public sealed class GetTransactionsQueryHandler
{
    private readonly ITransactionRepository _repository;

    public GetTransactionsQueryHandler(ITransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task<GetTransactionsResult> HandleAsync(GetTransactionsQuery query, CancellationToken cancellationToken)
    {
        var (totalCount, transactions) = await _repository.GetPagedAsync(
            query.CoupleId,
            query.Page,
            query.PageSize,
            query.Category,
            query.StartDate,
            query.EndDate,
            cancellationToken);

        var items = transactions
            .Select(t => new TransactionDto(
                t.Id,
                t.CoupleId,
                t.UserId,
                t.Bank,
                t.Amount,
                t.Currency,
                t.EventTimestampUtc,
                t.Description,
                t.Merchant,
                t.Category,
                t.CreatedAtUtc))
            .ToList();

        return new GetTransactionsResult(totalCount, query.Page, query.PageSize, items);
    }
}
