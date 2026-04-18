using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalsQueryHandler
{
    private readonly IGoalRepository _repository;

    public GetGoalsQueryHandler(IGoalRepository repository)
    {
        _repository = repository;
    }

    public async Task<GetGoalsResult> HandleAsync(GetGoalsQuery query, CancellationToken cancellationToken)
    {
        var (totalCount, goals) = await _repository.GetPagedAsync(
            query.CoupleId,
            query.IncludeArchived,
            cancellationToken);

        var items = goals
            .Select(g => new GoalDto(
                g.Id,
                g.CreatedByUserId,
                g.Title,
                g.Description,
                g.TargetAmount,
                g.Currency,
                g.Deadline,
                g.Status,
                g.CreatedAtUtc,
                g.UpdatedAtUtc))
            .ToList();

        return new GetGoalsResult(totalCount, items);
    }
}
