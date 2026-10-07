using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals;

/// <summary>Loads the linked-transaction totals (always at read time) and applies <see cref="GoalProgressBreakdown"/>.</summary>
public sealed class GoalProgressReader
{
    private readonly ITransactionRepository _transactionRepository;

    public GoalProgressReader(ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
    }

    public async Task<GoalProgressBreakdown> ReadAsync(Goal goal, CancellationToken ct)
    {
        var all = await ReadAsync(goal.CoupleId, new[] { goal }, ct);
        return all[goal.Id];
    }

    public async Task<IReadOnlyDictionary<Guid, GoalProgressBreakdown>> ReadAsync(
        Guid coupleId,
        IReadOnlyCollection<Goal> goals,
        CancellationToken ct)
    {
        if (goals.Count == 0)
            return new Dictionary<Guid, GoalProgressBreakdown>();

        var linked = await _transactionRepository.GetLinkedAmountsByGoalAsync(
            coupleId, goals.Select(g => g.Id).ToList(), ct);

        return goals.ToDictionary(
            g => g.Id,
            g => GoalProgressBreakdown.From(g, linked.TryGetValue(g.Id, out var amount) ? amount : 0m));
    }
}
