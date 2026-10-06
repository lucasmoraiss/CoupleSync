using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Transactions.Commands;

public sealed class DeleteTransactionCommandHandler
{
    private readonly ITransactionRepository _repository;

    public DeleteTransactionCommandHandler(ITransactionRepository repository)
    {
        _repository = repository;
    }

    public async Task HandleAsync(DeleteTransactionCommand command, CancellationToken cancellationToken)
    {
        var transaction = await _repository.GetByIdRawAsync(command.TransactionId, cancellationToken);

        if (transaction is null)
            throw new NotFoundException("TRANSACTION_NOT_FOUND", "Transação não encontrada.");

        if (transaction.CoupleId != command.CoupleId)
            throw new ForbiddenException("TRANSACTION_ACCESS_DENIED", "Você não tem acesso a esta transação.");

        await _repository.DeleteAsync(transaction, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }
}
