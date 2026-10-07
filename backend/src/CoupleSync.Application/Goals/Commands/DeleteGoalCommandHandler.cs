using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Commands;

public sealed class DeleteGoalCommandHandler
{
    private readonly IGoalRepository _repository;

    public DeleteGoalCommandHandler(IGoalRepository repository)
    {
        _repository = repository;
    }

    public async Task HandleAsync(DeleteGoalCommand command, CancellationToken cancellationToken)
    {
        var goal = await _repository.GetByIdAsync(command.Id, command.CoupleId, cancellationToken);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Meta não encontrada.");

        await _repository.DeleteAsync(goal, cancellationToken);
    }
}
