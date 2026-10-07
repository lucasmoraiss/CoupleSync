using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalByIdQueryHandler
{
    private readonly IGoalRepository _repository;
    private readonly GoalProgressReader _progressReader;

    public GetGoalByIdQueryHandler(IGoalRepository repository, GoalProgressReader progressReader)
    {
        _repository = repository;
        _progressReader = progressReader;
    }

    public async Task<GoalDto> HandleAsync(GetGoalByIdQuery query, CancellationToken cancellationToken)
    {
        var goal = await _repository.GetByIdAsync(query.Id, query.CoupleId, cancellationToken);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Meta não encontrada.");

        return GoalDto.From(goal, await _progressReader.ReadAsync(goal, cancellationToken));
    }
}
