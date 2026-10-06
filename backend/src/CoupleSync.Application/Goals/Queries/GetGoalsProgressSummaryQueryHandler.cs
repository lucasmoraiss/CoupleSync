using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalsProgressSummaryQueryHandler
{
    private readonly IGoalRepository _repository;
    private readonly GoalProgressReader _progressReader;

    public GetGoalsProgressSummaryQueryHandler(IGoalRepository repository, GoalProgressReader progressReader)
    {
        _repository = repository;
        _progressReader = progressReader;
    }

    public async Task<GetGoalsProgressSummaryResult> HandleAsync(
        GetGoalsProgressSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var (_, goals) = await _repository.GetPagedAsync(query.CoupleId, includeArchived: false, cancellationToken);

        var progress = await _progressReader.ReadAsync(query.CoupleId, goals.ToList(), cancellationToken);

        var items = goals
            .Select(g =>
            {
                var p = progress[g.Id];
                return new GoalProgressSummaryItem(
                    g.Id,
                    g.Title,
                    g.TargetAmount,
                    p.TotalAmount,
                    p.ProgressPercent,
                    p.IsAchieved,
                    g.Deadline,
                    p.ManualAmount,
                    p.LinkedAmount);
            })
            .ToList();

        return new GetGoalsProgressSummaryResult(items);
    }
}
