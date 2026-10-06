using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals.Queries;

namespace CoupleSync.Application.Goals.Commands;

public sealed class UpdateGoalCommandHandler
{
    private readonly IGoalRepository _repository;
    private readonly GoalProgressReader _progressReader;
    private readonly IDateTimeProvider _dateTimeProvider;

    public UpdateGoalCommandHandler(
        IGoalRepository repository,
        GoalProgressReader progressReader,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _progressReader = progressReader;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GoalDto> HandleAsync(UpdateGoalCommand command, CancellationToken cancellationToken)
    {
        var goal = await _repository.GetByIdAsync(command.Id, command.CoupleId, cancellationToken);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Meta não encontrada.");

        if (goal.Status == Domain.Entities.GoalStatus.Archived)
            throw new ConflictException("GOAL_ARCHIVED", "Não é possível editar uma meta arquivada.");

        var now = _dateTimeProvider.UtcNow;
        goal.Update(command.Title, command.Description, command.TargetAmount, command.Deadline, now);

        if (command.ManualAmount.HasValue)
        {
            goal.UpdateCurrentAmount(command.ManualAmount.Value, now);
        }
        else if (command.CurrentAmount.HasValue)
        {
            var linked = (await _progressReader.ReadAsync(goal, cancellationToken)).LinkedAmount;
            goal.UpdateCurrentAmount(Math.Max(0m, command.CurrentAmount.Value - linked), now);
        }

        await _repository.SaveChangesAsync(cancellationToken);

        return GoalDto.From(goal, await _progressReader.ReadAsync(goal, cancellationToken));
    }
}
