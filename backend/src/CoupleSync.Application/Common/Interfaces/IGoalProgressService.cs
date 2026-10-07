using CoupleSync.Application.Goals.Queries;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IGoalProgressService
{
    /// <summary>Progress = the goal's manual amount + <paramref name="linkedAmount"/> (sum of linked transactions).</summary>
    GoalProgressResult Compute(Goal goal, decimal linkedAmount, DateTime nowUtc);
}
