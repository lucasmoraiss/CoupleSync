using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalsProgressSummaryQueryHandler
{
    private readonly IGoalRepository _repository;

    public GetGoalsProgressSummaryQueryHandler(IGoalRepository repository)
    {
        _repository = repository;
    }

    public async Task<GetGoalsProgressSummaryResult> HandleAsync(
        GetGoalsProgressSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var (_, goals) = await _repository.GetPagedAsync(query.CoupleId, includeArchived: false, cancellationToken);

        var items = goals
            .Select(g =>
            {
                var progress = g.TargetAmount > 0
                    ? Math.Min(g.CurrentAmount / g.TargetAmount * 100m, 100m)
                    : 0m;

                return new GoalProgressSummaryItem(
                    g.Id,
                    g.Title,
                    g.TargetAmount,
                    g.CurrentAmount,
                    Math.Round(progress, 1),
                    g.CurrentAmount >= g.TargetAmount,
                    g.Deadline);
            })
            .ToList();

        return new GetGoalsProgressSummaryResult(items);
    }
}
