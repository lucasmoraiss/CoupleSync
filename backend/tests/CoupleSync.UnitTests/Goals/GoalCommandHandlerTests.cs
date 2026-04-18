using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Goals.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Goals;

public sealed class GoalCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureDeadline = FixedNow.AddDays(30);

    private static (CreateGoalCommandHandler, FakeGoalRepository) BuildCreateHandler()
    {
        var repo = new FakeGoalRepository();
        var dt = new FixedDateTimeProvider(FixedNow);
        return (new CreateGoalCommandHandler(repo, dt), repo);
    }

    private static (UpdateGoalCommandHandler, FakeGoalRepository) BuildUpdateHandler()
    {
        var repo = new FakeGoalRepository();
        var dt = new FixedDateTimeProvider(FixedNow);
        return (new UpdateGoalCommandHandler(repo, dt), repo);
    }

    private static (ArchiveGoalCommandHandler, FakeGoalRepository) BuildArchiveHandler()
    {
        var repo = new FakeGoalRepository();
        var dt = new FixedDateTimeProvider(FixedNow);
        return (new ArchiveGoalCommandHandler(repo, dt), repo);
    }

    private static Goal SeedGoal(FakeGoalRepository repo, Guid coupleId)
    {
        var goal = Goal.Create(
            coupleId,
            Guid.NewGuid(),
            "Vacation Fund",
            "Save for a trip",
            1000m,
            "BRL",
            FutureDeadline,
            FixedNow.AddDays(-1));
        repo.Goals.Add(goal);
        return goal;
    }

    // ── CreateGoal ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGoal_WithValidInput_ReturnsGoalDto()
    {
        var (handler, repo) = BuildCreateHandler();
        var coupleId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new CreateGoalCommand(coupleId, Guid.NewGuid(), "Emergency Fund", null, 500m, "BRL", FutureDeadline),
            CancellationToken.None);

        Assert.Equal("Emergency Fund", result.Title);
        Assert.Equal(500m, result.TargetAmount);
        Assert.Equal(GoalStatus.Active, result.Status);
        Assert.Single(repo.Goals);
    }

    [Fact]
    public async Task CreateGoal_WithEmptyTitle_Throws()
    {
        var (handler, _) = BuildCreateHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                new CreateGoalCommand(Guid.NewGuid(), Guid.NewGuid(), "", null, 500m, "BRL", FutureDeadline),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateGoal_WithZeroTargetAmount_Throws()
    {
        var (handler, _) = BuildCreateHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                new CreateGoalCommand(Guid.NewGuid(), Guid.NewGuid(), "Goal", null, 0m, "BRL", FutureDeadline),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateGoal_WithNegativeTargetAmount_Throws()
    {
        var (handler, _) = BuildCreateHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                new CreateGoalCommand(Guid.NewGuid(), Guid.NewGuid(), "Goal", null, -100m, "BRL", FutureDeadline),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateGoal_WithPastDeadline_Throws()
    {
        var (handler, _) = BuildCreateHandler();
        var pastDeadline = FixedNow.AddDays(-1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                new CreateGoalCommand(Guid.NewGuid(), Guid.NewGuid(), "Goal", null, 500m, "BRL", pastDeadline),
                CancellationToken.None));
    }

    // ── UpdateGoal ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGoal_WithValidInput_UpdatesAndReturnsDto()
    {
        var (handler, repo) = BuildUpdateHandler();
        var coupleId = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleId);

        var result = await handler.HandleAsync(
            new UpdateGoalCommand(goal.Id, coupleId, "New Title", null, null, null),
            CancellationToken.None);

        Assert.Equal("New Title", result.Title);
        Assert.Equal(goal.Id, result.Id);
    }

    [Fact]
    public async Task UpdateGoal_PartialUpdate_OnlyChangesProvidedFields()
    {
        var (handler, repo) = BuildUpdateHandler();
        var coupleId = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleId);
        var originalTitle = goal.Title;
        var originalAmount = goal.TargetAmount;

        var result = await handler.HandleAsync(
            new UpdateGoalCommand(goal.Id, coupleId, null, "Updated desc", null, null),
            CancellationToken.None);

        Assert.Equal(originalTitle, result.Title);
        Assert.Equal(originalAmount, result.TargetAmount);
        Assert.Equal("Updated desc", result.Description);
    }

    [Fact]
    public async Task UpdateGoal_UnknownGoalId_ThrowsNotFoundException()
    {
        var (handler, _) = BuildUpdateHandler();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new UpdateGoalCommand(Guid.NewGuid(), Guid.NewGuid(), "T", null, null, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateGoal_OtherCoupleGoal_ThrowsNotFoundException()
    {
        var (handler, repo) = BuildUpdateHandler();
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleA);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new UpdateGoalCommand(goal.Id, coupleB, "Hacked", null, null, null),
                CancellationToken.None));
    }

    // ── ArchiveGoal ────────────────────────────────────────────────────────

    [Fact]
    public async Task ArchiveGoal_WithValidGoal_MarksArchived()
    {
        var (handler, repo) = BuildArchiveHandler();
        var coupleId = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleId);

        var result = await handler.HandleAsync(
            new ArchiveGoalCommand(goal.Id, coupleId),
            CancellationToken.None);

        Assert.Equal(GoalStatus.Archived, result.Status);
        Assert.Equal(GoalStatus.Archived, goal.Status);
    }

    [Fact]
    public async Task ArchiveGoal_UnknownGoalId_ThrowsNotFoundException()
    {
        var (handler, _) = BuildArchiveHandler();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new ArchiveGoalCommand(Guid.NewGuid(), Guid.NewGuid()),
                CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveGoal_OtherCoupleGoal_ThrowsNotFoundException()
    {
        var (handler, repo) = BuildArchiveHandler();
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleA);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new ArchiveGoalCommand(goal.Id, coupleB),
                CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveGoal_AlreadyArchivedGoal_IsIdempotentAndDoesNotUpdateTimestamp()
    {
        var (handler, repo) = BuildArchiveHandler();
        var coupleId = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleId);

        // First archive
        var first = await handler.HandleAsync(
            new ArchiveGoalCommand(goal.Id, coupleId),
            CancellationToken.None);

        var timestampAfterFirstArchive = goal.UpdatedAtUtc;

        // Second archive — should be idempotent
        var second = await handler.HandleAsync(
            new ArchiveGoalCommand(goal.Id, coupleId),
            CancellationToken.None);

        Assert.Equal(GoalStatus.Archived, second.Status);
        Assert.Equal(timestampAfterFirstArchive, goal.UpdatedAtUtc);
    }

    [Fact]
    public async Task UpdateGoal_WithPastDeadline_ThrowsArgumentException()
    {
        var (handler, repo) = BuildUpdateHandler();
        var coupleId = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleId);
        var pastDeadline = FixedNow.AddDays(-1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                new UpdateGoalCommand(goal.Id, coupleId, null, null, null, pastDeadline),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateGoal_ArchivedGoal_ThrowsConflictException()
    {
        var (handler, repo) = BuildUpdateHandler();
        var coupleId = Guid.NewGuid();
        var goal = SeedGoal(repo, coupleId);
        goal.Archive(FixedNow);

        await Assert.ThrowsAsync<ConflictException>(() =>
            handler.HandleAsync(
                new UpdateGoalCommand(goal.Id, coupleId, "New Title", null, null, null),
                CancellationToken.None));
    }
}
