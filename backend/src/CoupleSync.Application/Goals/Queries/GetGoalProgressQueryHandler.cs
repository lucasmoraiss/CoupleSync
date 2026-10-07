using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalProgressQueryHandler
{
    private readonly IGoalRepository _goalRepository;
    private readonly GoalProgressReader _progressReader;
    private readonly IGoalProgressService _progressService;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetGoalProgressQueryHandler(
        IGoalRepository goalRepository,
        GoalProgressReader progressReader,
        IGoalProgressService progressService,
        IDateTimeProvider dateTimeProvider)
    {
        _goalRepository = goalRepository;
        _progressReader = progressReader;
        _progressService = progressService;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GoalProgressResult> HandleAsync(GetGoalProgressQuery query, CancellationToken ct)
    {
        var goal = await _goalRepository.GetByIdAsync(query.GoalId, query.CoupleId, ct);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Meta não encontrada.");

        var progress = await _progressReader.ReadAsync(goal, ct);

        return _progressService.Compute(goal, progress.LinkedAmount, _dateTimeProvider.UtcNow);
    }
}
