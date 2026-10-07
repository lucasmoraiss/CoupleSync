using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals.Queries;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals;

public sealed class GoalProgressService : IGoalProgressService
{
    public GoalProgressResult Compute(Goal goal, decimal linkedAmount, DateTime nowUtc)
    {
        var progress = GoalProgressBreakdown.From(goal, linkedAmount);
        var daysRemaining = (goal.Deadline - nowUtc).TotalDays;

        return new GoalProgressResult(
            goal.Id,
            goal.Title,
            goal.TargetAmount,
            progress.TotalAmount,
            progress.ProgressPercent,
            progress.IsAchieved,
            daysRemaining,
            goal.Status,
            progress.ManualAmount,
            progress.LinkedAmount);
    }
}
