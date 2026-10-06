using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Goals;
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

    private static GoalProgressReader ReaderFor(FakeTransactionRepository txRepo) => new(txRepo);

    private static Transaction LinkedTx(Guid coupleId, Guid goalId, decimal amount, string currency = "BRL")
    {
        var tx = Transaction.Create(coupleId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), "NUBANK", amount, currency,
            FixedNow.AddDays(-1), null, null, "Outros", Guid.NewGuid(), FixedNow);
        tx.LinkToGoal(goalId);
        return tx;
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

        var handler = new GetGoalsQueryHandler(repo, ReaderFor(new FakeTransactionRepository()));
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

        var handler = new GetGoalsQueryHandler(repo, ReaderFor(new FakeTransactionRepository()));
        var result = await handler.HandleAsync(new GetGoalsQuery(coupleId, true), CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task GetGoals_EmptyRepo_ReturnsEmptyResult()
    {
        var repo = new FakeGoalRepository();
        var coupleId = Guid.NewGuid();

        var handler = new GetGoalsQueryHandler(repo, ReaderFor(new FakeTransactionRepository()));
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

        var handler = new GetGoalByIdQueryHandler(repo, ReaderFor(new FakeTransactionRepository()));
        var result = await handler.HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), CancellationToken.None);

        Assert.Equal(goal.Id, result.Id);
        Assert.Equal(goal.Title, result.Title);
    }

    [Fact]
    public async Task GetGoalById_NotFound_ThrowsNotFoundException()
    {
        var repo = new FakeGoalRepository();

        var handler = new GetGoalByIdQueryHandler(repo, ReaderFor(new FakeTransactionRepository()));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(new GetGoalByIdQuery(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }

    // ── Progresso único: manual + transações vinculadas ────────────────────

    [Fact]
    public async Task AllEndpoints_ReportTheSameProgress_ManualPlusLinkedTransactions()
    {
        var repo = new FakeGoalRepository();
        var txRepo = new FakeTransactionRepository();
        var reader = ReaderFor(txRepo);
        var coupleId = Guid.NewGuid();
        var goal = MakeGoal(coupleId); // alvo 500
        goal.UpdateCurrentAmount(100m, FixedNow);
        repo.Goals.Add(goal);
        txRepo.Transactions.Add(LinkedTx(coupleId, goal.Id, 150m));
        txRepo.Transactions.Add(LinkedTx(coupleId, goal.Id, 50m));
        txRepo.Transactions.Add(LinkedTx(coupleId, goal.Id, 999m, currency: "USD")); // fora da soma em reais
        txRepo.Transactions.Add(LinkedTx(Guid.NewGuid(), goal.Id, 777m));            // outro grupo

        var list = (await new GetGoalsQueryHandler(repo, reader).HandleAsync(new GetGoalsQuery(coupleId, false), default)).Items.Single();
        var byId = await new GetGoalByIdQueryHandler(repo, reader).HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), default);
        var summary = (await new GetGoalsProgressSummaryQueryHandler(repo, reader).HandleAsync(new GetGoalsProgressSummaryQuery(coupleId), default)).Goals.Single();
        var progress = await new GetGoalProgressQueryHandler(repo, reader, new GoalProgressService(), new FixedDateTimeProvider(FixedNow))
            .HandleAsync(new GetGoalProgressQuery(goal.Id, coupleId), default);

        Assert.Equal(300m, list.CurrentAmount);
        Assert.Equal(100m, list.ManualAmount);
        Assert.Equal(200m, list.LinkedAmount);
        Assert.Equal(60m, list.ProgressPercent);
        Assert.False(list.IsAchieved);

        Assert.Equal(list.CurrentAmount, byId.CurrentAmount);
        Assert.Equal(list.CurrentAmount, summary.CurrentAmount);
        Assert.Equal(list.CurrentAmount, progress.ContributedAmount);
        Assert.Equal(list.ProgressPercent, byId.ProgressPercent);
        Assert.Equal(list.ProgressPercent, summary.ProgressPercent);
        Assert.Equal(list.ProgressPercent, progress.ProgressPercent);
        Assert.Equal(list.ManualAmount, summary.ManualAmount);
        Assert.Equal(list.LinkedAmount, progress.LinkedAmount);
    }

    [Fact]
    public async Task AllEndpoints_FlagAchievedGoalTheSameWay()
    {
        var repo = new FakeGoalRepository();
        var txRepo = new FakeTransactionRepository();
        var reader = ReaderFor(txRepo);
        var coupleId = Guid.NewGuid();
        var goal = MakeGoal(coupleId); // alvo 500
        goal.UpdateCurrentAmount(200m, FixedNow);
        repo.Goals.Add(goal);
        txRepo.Transactions.Add(LinkedTx(coupleId, goal.Id, 300m)); // 200 + 300 = alvo exato

        var list = (await new GetGoalsQueryHandler(repo, reader).HandleAsync(new GetGoalsQuery(coupleId, false), default)).Items.Single();
        var byId = await new GetGoalByIdQueryHandler(repo, reader).HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), default);
        var summary = (await new GetGoalsProgressSummaryQueryHandler(repo, reader).HandleAsync(new GetGoalsProgressSummaryQuery(coupleId), default)).Goals.Single();
        var progress = await new GetGoalProgressQueryHandler(repo, reader, new GoalProgressService(), new FixedDateTimeProvider(FixedNow))
            .HandleAsync(new GetGoalProgressQuery(goal.Id, coupleId), default);

        Assert.True(list.IsAchieved);
        Assert.True(byId.IsAchieved);
        Assert.True(summary.IsAchieved);
        Assert.True(progress.IsAchieved);
        Assert.Equal(100m, list.ProgressPercent);
    }

    [Fact]
    public async Task UnlinkingOrDeletingALinkedTransaction_IsReflectedOnTheNextRead()
    {
        var repo = new FakeGoalRepository();
        var txRepo = new FakeTransactionRepository();
        var handler = new GetGoalByIdQueryHandler(repo, ReaderFor(txRepo));
        var coupleId = Guid.NewGuid();
        var goal = MakeGoal(coupleId);
        repo.Goals.Add(goal);
        var tx1 = LinkedTx(coupleId, goal.Id, 100m);
        var tx2 = LinkedTx(coupleId, goal.Id, 50m);
        txRepo.Transactions.Add(tx1);
        txRepo.Transactions.Add(tx2);

        Assert.Equal(150m, (await handler.HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), default)).CurrentAmount);

        tx1.LinkToGoal(null);
        Assert.Equal(50m, (await handler.HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), default)).CurrentAmount);

        txRepo.Transactions.Remove(tx2);
        Assert.Equal(0m, (await handler.HandleAsync(new GetGoalByIdQuery(goal.Id, coupleId), default)).CurrentAmount);
    }

    [Fact]
    public void Percent_IsTruncated_SoAlmostThereNeverShowsAs100()
    {
        var goal = Goal.Create(Guid.NewGuid(), Guid.NewGuid(), "Meta", null, 10000m, "BRL", FutureDeadline, FixedNow);
        goal.UpdateCurrentAmount(9996m, FixedNow);

        var p = GoalProgressBreakdown.From(goal, 0m);

        Assert.Equal(99.9m, p.ProgressPercent);
        Assert.False(p.IsAchieved);
    }}
