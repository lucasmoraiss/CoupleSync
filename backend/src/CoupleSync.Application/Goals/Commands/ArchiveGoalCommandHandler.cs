using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals.Queries;

namespace CoupleSync.Application.Goals.Commands;

public sealed class ArchiveGoalCommandHandler
{
    private readonly IGoalRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public ArchiveGoalCommandHandler(IGoalRepository repository, IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GoalDto> HandleAsync(ArchiveGoalCommand command, CancellationToken cancellationToken)
    {
        var goal = await _repository.GetByIdAsync(command.Id, command.CoupleId, cancellationToken);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Goal not found.");

        var now = _dateTimeProvider.UtcNow;
        goal.Archive(now);

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
