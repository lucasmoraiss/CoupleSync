using CoupleSync.Application.Goals.Queries;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IGoalProgressService
{
    GoalProgressResult Compute(Goal goal, decimal contributedAmount, DateTime nowUtc);
}
