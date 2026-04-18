using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals.Queries;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals;

public sealed class GoalProgressService : IGoalProgressService
{
    public GoalProgressResult Compute(Goal goal, decimal contributedAmount, DateTime nowUtc)
    {
        var progressPercent = Math.Clamp(contributedAmount / goal.TargetAmount * 100m, 0m, 100m);

        var isAchieved = contributedAmount >= goal.TargetAmount;
        var daysRemaining = (goal.Deadline - nowUtc).TotalDays;

        return new GoalProgressResult(
            goal.Id,
            goal.Title,
            goal.TargetAmount,
            contributedAmount,
            progressPercent,
            isAchieved,
            daysRemaining,
            goal.Status);
    }
}
