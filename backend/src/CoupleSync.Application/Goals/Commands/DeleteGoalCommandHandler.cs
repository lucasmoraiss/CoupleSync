using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Goals.Commands;

public sealed class DeleteGoalCommandHandler
{
    private readonly IGoalRepository _repository;
    private readonly IQueryDbContext _dbContext;

    public DeleteGoalCommandHandler(IGoalRepository repository, IQueryDbContext dbContext)
    {
        _repository = repository;
        _dbContext = dbContext;
    }

    public async Task HandleAsync(DeleteGoalCommand command, CancellationToken cancellationToken)
    {
        var goal = await _repository.GetByIdAsync(command.Id, command.CoupleId, cancellationToken);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Goal not found.");

        // Nullify GoalId on all transactions linked to this goal (bulk update, no tracking)
        await _dbContext.Transactions
            .Where(t => t.GoalId == command.Id && t.CoupleId == command.CoupleId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.GoalId, (Guid?)null),
                cancellationToken);

        _dbContext.Goals.Remove(goal);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
