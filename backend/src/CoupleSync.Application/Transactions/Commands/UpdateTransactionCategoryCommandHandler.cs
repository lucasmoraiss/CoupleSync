using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Transactions.Commands;

public sealed class UpdateTransactionCategoryCommandHandler
{
    private readonly ITransactionRepository _repository;

    public UpdateTransactionCategoryCommandHandler(ITransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task<UpdateTransactionCategoryResult> HandleAsync(
        UpdateTransactionCategoryCommand command,
        CancellationToken cancellationToken)
    {
        var transaction = await _repository.GetByIdAsync(command.TransactionId, command.CoupleId, cancellationToken);

        if (transaction is null)
            throw new NotFoundException("TRANSACTION_NOT_FOUND", "Transaction not found.");

        transaction.UpdateCategory(command.Category);
        await _repository.SaveChangesAsync(cancellationToken);

        return new UpdateTransactionCategoryResult(
            transaction.Id,
            transaction.CoupleId,
            transaction.UserId,
            transaction.Bank,
            transaction.Amount,
            transaction.Currency,
            transaction.EventTimestampUtc,
            transaction.Description,
            transaction.Merchant,
            transaction.Category,
            transaction.CreatedAtUtc);
    }
}
