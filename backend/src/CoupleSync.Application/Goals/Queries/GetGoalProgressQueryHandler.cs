using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Goals.Queries;

public sealed class GetGoalProgressQueryHandler
{
    private readonly IGoalRepository _goalRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IGoalProgressService _progressService;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetGoalProgressQueryHandler(
        IGoalRepository goalRepository,
        ITransactionRepository transactionRepository,
        IGoalProgressService progressService,
        IDateTimeProvider dateTimeProvider)
    {
        _goalRepository = goalRepository;
        _transactionRepository = transactionRepository;
        _progressService = progressService;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GoalProgressResult> HandleAsync(GetGoalProgressQuery query, CancellationToken ct)
    {
        var goal = await _goalRepository.GetByIdAsync(query.GoalId, query.CoupleId, ct);

        if (goal is null)
            throw new NotFoundException("GOAL_NOT_FOUND", "Goal not found.");

        var transactions = await _transactionRepository.GetByGoalIdAsync(query.GoalId, query.CoupleId, ct);
        var contributedAmount = transactions.Sum(t => t.Amount);

        return _progressService.Compute(goal, contributedAmount, _dateTimeProvider.UtcNow);
    }
}
