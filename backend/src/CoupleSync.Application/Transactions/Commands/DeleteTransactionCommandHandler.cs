using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Transactions.Commands;

public sealed class DeleteTransactionCommandHandler
{
    private readonly ITransactionRepository _repository;
    private readonly IBankSyncRepository _bankSync;
    private readonly IDateTimeProvider _clock;

    public DeleteTransactionCommandHandler(
        ITransactionRepository repository,
        IBankSyncRepository bankSync,
        IDateTimeProvider clock)
    {
        _repository = repository;
        _bankSync = bankSync;
        _clock = clock;
    }

    public async Task HandleAsync(DeleteTransactionCommand command, CancellationToken cancellationToken)
    {
        var transaction = await _repository.GetByIdRawAsync(command.TransactionId, cancellationToken);

        if (transaction is null)
            throw new NotFoundException("TRANSACTION_NOT_FOUND", "Transação não encontrada.");

        if (transaction.CoupleId != command.CoupleId)
            throw new ForbiddenException("TRANSACTION_ACCESS_DENIED", "Você não tem acesso a esta transação.");

        // A transaction a line of the bank points to (Open Finance): created from the line, or one the person said
        // the line was the same as. The line goes back to the review as discarded, without the link, in the same
        // save. It can be restored and confirmed again.
        var now = _clock.UtcNow;
        foreach (var line in await _bankSync.FindAllByLinkedTransactionAsync(transaction.Id, transaction.CoupleId, cancellationToken))
            line.Discard(now);

        await _repository.DeleteAsync(transaction, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }
}
