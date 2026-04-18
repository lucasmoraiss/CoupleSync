using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Goals.Queries;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Goals;

public sealed class GoalQueryHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureDeadline = FixedNow.AddDays(30);

    private static Goal MakeGoal(Guid coupleId, string title = "Vacation Fund", GoalStatus status = GoalStatus.Active)
    {
        var goal = Goal.Create(
            coupleId,
            Guid.NewGuid(),
            title,
            null,
            500m,
            "BRL",
            FutureDeadline,
            FixedNow.AddDays(-1));

        if (status == GoalStatus.Archived)
            goal.Archive(FixedNow);

        return goal;
    }

    // ── GetGoals ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoals_ReturnsActiveGoalsOnly()
    {
        var repo = new FakeGoalRepository();
        var coupleId = Guid.NewGuid();
        var active = MakeGoal(coupleId, "Active Goal");
        var archived = MakeGoal(coupleId, "Archived Goal", GoalStatus.Archived);
        repo.Goals.Add(active);
        repo.Goals.Add(archived);

        var handler = new GetGoalsQueryHandler(repo);
        var result = await handler.HandleAsync(new GetGoalsQuery(coupleId, false), CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Active Goal", result.Items[0].Title);
    }

    [Fact]
    public async Task GetGoals_IncludeArchived_ReturnsBoth()
    {
        var repo = new FakeGoalRepository();
        var coupleId = Guid.NewGuid();
        repo.Goals.Add(MakeGoal(coupleId, "Active Goal"));
        repo.Goals.Add(MakeGoal(coupleId, "Archived Goal", GoalStatus.Archived));

        var handler = new GetGoalsQueryHandler(repo);
        var result = await handler.HandleAsync(new GetGoalsQuery(coupleId, true), CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task GetGoals_EmptyRepo_ReturnsEmptyResult()
    {
        var repo = new FakeGoalRepository();
        var coupleId = Guid.NewGuid();

        var handler = new GetGoalsQueryHandler(repo);
        var result = await handler.HandleAsync(new GetGoalsQuery(coupleId, false), CancellationToken.None);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    // ── GetGoalById ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoalById_Found_ReturnsDto()
    {
        var repo = new FakeGoalRepository();
        var coupleId = Guid.NewGuid();
        var goal = MakeGoal(coupleId);
        repo.Goals.Add(goal);

        var handler = new GetGoalByIdQueryHandler(repo);
        var result = await handler.HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), CancellationToken.None);

        Assert.Equal(goal.Id, result.Id);
        Assert.Equal(goal.Title, result.Title);
    }

    [Fact]
    public async Task GetGoalById_NotFound_ThrowsNotFoundException()
    {
        var repo = new FakeGoalRepository();

        var handler = new GetGoalByIdQueryHandler(repo);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(new GetGoalByIdQuery(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }
}
