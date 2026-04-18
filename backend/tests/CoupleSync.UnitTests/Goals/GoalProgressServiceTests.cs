using CoupleSync.Application.Goals;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Goals;

public sealed class GoalProgressServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureDeadline = FixedNow.AddDays(30);

    private static Goal MakeGoal(
        decimal targetAmount = 1000m,
        GoalStatus status = GoalStatus.Active,
        DateTime? deadline = null)
    {
        var goal = Goal.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Vacation Fund",
            null,
            targetAmount,
            "BRL",
            deadline ?? FutureDeadline,
            FixedNow.AddDays(-1));

        if (status == GoalStatus.Archived)
            goal.Archive(FixedNow);

        return goal;
    }

    private static GoalProgressService BuildService() => new();

    [Fact]
    public void Compute_ZeroContributions_Returns0Percent()
    {
        var goal = MakeGoal(targetAmount: 1000m);
        var svc = BuildService();

        var result = svc.Compute(goal, 0m, FixedNow);

        Assert.Equal(0m, result.ProgressPercent);
        Assert.Equal(0m, result.ContributedAmount);
        Assert.False(result.IsAchieved);
    }

    [Fact]
    public void Compute_HalfContributed_Returns50Percent()
    {
        var goal = MakeGoal(targetAmount: 1000m);
        var svc = BuildService();

        var result = svc.Compute(goal, 500m, FixedNow);

        Assert.Equal(50m, result.ProgressPercent);
        Assert.Equal(500m, result.ContributedAmount);
        Assert.False(result.IsAchieved);
    }

    [Fact]
    public void Compute_ExactTarget_Returns100PercentAndAchieved()
    {
        var goal = MakeGoal(targetAmount: 1000m);
        var svc = BuildService();

        var result = svc.Compute(goal, 1000m, FixedNow);

        Assert.Equal(100m, result.ProgressPercent);
        Assert.True(result.IsAchieved);
    }

    [Fact]
    public void Compute_Overachieved_ClampsAt100Percent()
    {
        var goal = MakeGoal(targetAmount: 1000m);
        var svc = BuildService();

        var result = svc.Compute(goal, 1500m, FixedNow);

        Assert.Equal(100m, result.ProgressPercent);
        Assert.Equal(1500m, result.ContributedAmount);
        Assert.True(result.IsAchieved);
    }

    [Fact]
    public void Compute_DaysRemaining_PositiveWhenDeadlineInFuture()
    {
        var goal = MakeGoal(deadline: FixedNow.AddDays(10));
        var svc = BuildService();

        var result = svc.Compute(goal, 0m, FixedNow);

        Assert.True(result.DaysRemaining > 0);
        Assert.InRange(result.DaysRemaining, 9.9, 10.1);
    }

    [Fact]
    public void Compute_DaysRemaining_NegativeWhenOverdue()
    {
        var goal = MakeGoal(deadline: FixedNow.AddDays(1)); // deadline is 1 day past now
        var svc = BuildService();

        // Simulate now being 5 days after deadline
        var overdue = FixedNow.AddDays(6);
        var result = svc.Compute(goal, 0m, overdue);

        Assert.True(result.DaysRemaining < 0);
    }

    [Fact]
    public void Compute_ArchivedGoal_ReturnsArchivedStatus()
    {
        var goal = MakeGoal(status: GoalStatus.Archived);
        var svc = BuildService();

        var result = svc.Compute(goal, 200m, FixedNow);

        Assert.Equal(GoalStatus.Archived, result.Status);
        Assert.Equal(200m, result.ContributedAmount);
    }

    [Fact]
    public void Compute_ActiveGoal_ReturnsActiveStatus()
    {
        var goal = MakeGoal(status: GoalStatus.Active);
        var svc = BuildService();

        var result = svc.Compute(goal, 0m, FixedNow);

        Assert.Equal(GoalStatus.Active, result.Status);
    }

    [Fact]
    public void Compute_ReturnsCorrectGoalIdAndTitle()
    {
        var goal = MakeGoal();
        var svc = BuildService();

        var result = svc.Compute(goal, 100m, FixedNow);

        Assert.Equal(goal.Id, result.GoalId);
        Assert.Equal("Vacation Fund", result.Title);
        Assert.Equal(1000m, result.TargetAmount);
    }
}
