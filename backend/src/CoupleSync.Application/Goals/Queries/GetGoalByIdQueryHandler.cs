using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalByIdQueryHandler
{
    private readonly IGoalRepository _repository;

    public GetGoalByIdQueryHandler(IGoalRepository repository)
    {
        _repository = repository;
    }

    public async Task<GoalDto> HandleAsync(GetGoalByIdQuery query, CancellationToken cancellationToken)
    {
        var goal = await _repository.GetByIdAsync(query.Id, query.CoupleId, cancellationToken);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Goal not found.");

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
