using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Transactions.Queries;

public sealed class GetTransactionsQueryHandler
{
    private readonly ITransactionRepository _repository;
    private readonly IQueryDbContext _dbContext;

    public GetTransactionsQueryHandler(ITransactionRepository repository, IQueryDbContext dbContext)
    {
        _repository = repository;
        _dbContext = dbContext;
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

        var userNameMap = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.CoupleId == query.CoupleId)
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);

        var items = transactions
            .Select(t => new TransactionDto(
                t.Id,
                t.CoupleId,
                t.UserId,
                userNameMap.GetValueOrDefault(t.UserId, "Desconhecido"),
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
