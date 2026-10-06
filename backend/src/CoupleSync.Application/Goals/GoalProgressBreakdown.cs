using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals;

/// <summary>
/// The one rule for goal progress: manually saved amount + sum of the (BRL) transactions linked to the goal.
/// Every endpoint that returns a goal builds its numbers here, so they can never disagree.
/// </summary>
public sealed record GoalProgressBreakdown(
    decimal ManualAmount,
    decimal LinkedAmount,
    decimal TotalAmount,
    decimal ProgressPercent,
    bool IsAchieved)
{
    public static GoalProgressBreakdown From(Goal goal, decimal linkedAmount)
    {
        var manual = goal.CurrentAmount;
        var total = manual + linkedAmount;

        // Truncated (not rounded) to one decimal so 99.96% never shows as 100% while the goal is not achieved.
        var percent = goal.TargetAmount > 0
            ? Math.Round(Math.Clamp(total / goal.TargetAmount * 100m, 0m, 100m), 1, MidpointRounding.ToZero)
            : 0m;

        return new GoalProgressBreakdown(manual, linkedAmount, total, percent, total >= goal.TargetAmount);
    }
}
