using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Commands;

public sealed class LinkTransactionToGoalCommandHandler
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly IGoalRepository _goalRepository;

    public LinkTransactionToGoalCommandHandler(
        ITransactionRepository transactionRepository,
        IGoalRepository goalRepository)
    {
        _transactionRepository = transactionRepository;
        _goalRepository = goalRepository;
    }

    public async Task HandleAsync(LinkTransactionToGoalCommand command, CancellationToken cancellationToken)
    {
        var transaction = await _transactionRepository.GetByIdAsync(
            command.TransactionId, command.CoupleId, cancellationToken);

        if (transaction is null)
            throw new NotFoundException("TRANSACTION_NOT_FOUND", "Transaction not found.");

        if (command.GoalId is not null)
        {
            var goal = await _goalRepository.GetByIdAsync(
                command.GoalId.Value, command.CoupleId, cancellationToken);

            if (goal is null)
                throw new NotFoundException("GOAL_NOT_FOUND", "Goal not found.");
        }

        transaction.LinkToGoal(command.GoalId);

        await _transactionRepository.UpdateAsync(transaction, cancellationToken);
        await _transactionRepository.SaveChangesAsync(cancellationToken);
    }
}
