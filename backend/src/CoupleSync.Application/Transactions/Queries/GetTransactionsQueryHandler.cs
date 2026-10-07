using CoupleSync.Domain.ValueObjects;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Transactions.Queries;

public sealed class GetTransactionsQueryHandler
{
    private readonly ITransactionRepository _repository;
    private readonly ICoupleRepository _coupleRepository;

    public GetTransactionsQueryHandler(ITransactionRepository repository, ICoupleRepository coupleRepository)
    {
        _repository = repository;
        _coupleRepository = coupleRepository;
    }

    public async Task<GetTransactionsResult> HandleAsync(GetTransactionsQuery query, CancellationToken cancellationToken)
    {
        var (totalCount, transactions) = await _repository.GetPagedAsync(
            query.CoupleId,
            query.Page,
            query.PageSize,
            // A filter typed as "Alimentação" or "alimentacao" means the canonical key.
            TransactionCategories.TryNormalize(query.Category) ?? query.Category,
            query.StartDate,
            query.EndDate,
            cancellationToken);

        var userNameMap = await _coupleRepository.GetMemberNamesAsync(query.CoupleId, cancellationToken);

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
                t.Source.ToString(),
                t.CreatedAtUtc))
            .ToList();

        return new GetTransactionsResult(totalCount, query.Page, query.PageSize, items);
    }
}
