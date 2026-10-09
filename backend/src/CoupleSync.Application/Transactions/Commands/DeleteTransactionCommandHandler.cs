using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OpenFinance;
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

        // The lines that only looked like it (suspected duplicates still waiting): the store clears their suspicion
        // with the deletion; which ones they were is read before.
        var lookedLikeIt = await _bankSync.FindWaitingIdsBySuspectAsync(transaction.Id, transaction.CoupleId, cancellationToken);

        await _repository.DeleteAsync(transaction, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        if (lookedLikeIt.Count > 0)
            await JudgeAgainAsync(command.CoupleId, lookedLikeIt, now, cancellationToken);
    }

    /// <summary>
    /// The transaction is gone. Each line that looked like it is judged again: it points to another transaction that
    /// looks like it, or waits as an ordinary expense. A second save, after the deletion (the same order of locks as
    /// "it is the same": the transaction first, then the lines). It never fails the deletion, which is stored: a
    /// line answered at this very moment keeps that answer, and the others are tried once more.
    /// </summary>
    private async Task JudgeAgainAsync(Guid coupleId, IReadOnlyCollection<Guid> lineIds, DateTime now, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var lines = await _bankSync.FindByIdsAsync(lineIds, coupleId, ct);
                var names = await _bankSync.GetAccountNamesAsync(coupleId, ct);
                await BankSuspicionJudge.JudgeAsync(
                    _bankSync, coupleId, lines, BankSuspicionJudge.ConnectorNameBy(names),
                    leavingLineIds: [], takenTransactionIds: [], now, ct);
                await _bankSync.SaveChangesAsync(ct);
                return;
            }
            catch (DataStoreException)
            {
                _bankSync.DiscardChanges();
            }
        }
    }
}
