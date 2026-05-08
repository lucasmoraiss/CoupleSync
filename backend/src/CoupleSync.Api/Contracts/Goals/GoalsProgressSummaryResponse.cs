using CoupleSync.Application.Goals.Queries;

namespace CoupleSync.Api.Contracts.Goals;

public sealed record GoalsProgressSummaryResponse(IReadOnlyList<GoalProgressSummaryItem> Goals);
