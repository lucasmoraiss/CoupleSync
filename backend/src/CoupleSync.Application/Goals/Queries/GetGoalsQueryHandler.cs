using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalsQueryHandler
{
    private readonly IGoalRepository _repository;
    private readonly GoalProgressReader _progressReader;

    public GetGoalsQueryHandler(IGoalRepository repository, GoalProgressReader progressReader)
    {
        _repository = repository;
        _progressReader = progressReader;
    }

    public async Task<GetGoalsResult> HandleAsync(GetGoalsQuery query, CancellationToken cancellationToken)
    {
        var (totalCount, goals) = await _repository.GetPagedAsync(
            query.CoupleId,
            query.IncludeArchived,
            cancellationToken);

        var progress = await _progressReader.ReadAsync(query.CoupleId, goals.ToList(), cancellationToken);

        var items = goals
            .Select(g => GoalDto.From(g, progress[g.Id]))
            .ToList();

        return new GetGoalsResult(totalCount, items);
    }
}
