using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals.Queries;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals.Commands;

public sealed class CreateGoalCommandHandler
{
    private readonly IGoalRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public CreateGoalCommandHandler(IGoalRepository repository, IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GoalDto> HandleAsync(CreateGoalCommand command, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;

        var goal = Goal.Create(
            command.CoupleId,
            command.CreatedByUserId,
            command.Title,
            command.Description,
            command.TargetAmount,
            command.Currency,
            command.Deadline,
            now);

        await _repository.AddAsync(goal, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        return new GoalDto(
            goal.Id,
            goal.CreatedByUserId,
            goal.Title,
            goal.Description,
            goal.TargetAmount,
            goal.Currency,
            goal.Deadline,
            goal.Status,
            goal.CreatedAtUtc,
            goal.UpdatedAtUtc);
    }
}
