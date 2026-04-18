using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Goals.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Goals;

public sealed class GoalLinkCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureDeadline = FixedNow.AddDays(30);

    private static (LinkTransactionToGoalCommandHandler Handler, FakeTransactionRepository TxRepo, FakeGoalRepository GoalRepo) BuildHandler()
    {
        var txRepo = new FakeTransactionRepository();
        var goalRepo = new FakeGoalRepository();
        return (new LinkTransactionToGoalCommandHandler(txRepo, goalRepo), txRepo, goalRepo);
    }

    private static Transaction BuildTransaction(Guid coupleId)
    {
        return Transaction.Create(
            coupleId,
            Guid.NewGuid(),
            $"fp-{Guid.NewGuid():N}",
            "NUBANK",
            100m,
            "BRL",
            new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc),
            "Test purchase",
            "Test Store",
            "OUTROS",
            Guid.NewGuid(),
            FixedNow);
    }

    private static Goal SeedGoal(FakeGoalRepository repo, Guid coupleId)
    {
        var goal = Goal.Create(
            coupleId,
            Guid.NewGuid(),
            "Vacation Fund",
            null,
            1000m,
            "BRL",
            FutureDeadline,
            FixedNow.AddDays(-1));
        repo.Goals.Add(goal);
        return goal;
    }

    // ── LinkTransactionToGoal ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenValidTransactionAndGoal_LinksAndSaves()
    {
        var (handler, txRepo, goalRepo) = BuildHandler();
        var coupleId = Guid.NewGuid();
        var transaction = BuildTransaction(coupleId);
        txRepo.Transactions.Add(transaction);
        var goal = SeedGoal(goalRepo, coupleId);

        await handler.HandleAsync(
            new LinkTransactionToGoalCommand(transaction.Id, goal.Id, coupleId),
            CancellationToken.None);

        Assert.Equal(goal.Id, transaction.GoalId);
        Assert.True(txRepo.UpdateCalled);
    }

    [Fact]
    public async Task HandleAsync_WhenTransactionNotFound_ThrowsNotFoundException()
    {
        var (handler, _, _) = BuildHandler();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new LinkTransactionToGoalCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTransactionFromOtherCouple_ThrowsNotFoundException()
    {
        var (handler, txRepo, _) = BuildHandler();
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        var transaction = BuildTransaction(coupleA);
        txRepo.Transactions.Add(transaction);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new LinkTransactionToGoalCommand(transaction.Id, Guid.NewGuid(), coupleB),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenGoalFromOtherCouple_ThrowsNotFoundException()
    {
        var (handler, txRepo, goalRepo) = BuildHandler();
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        var transaction = BuildTransaction(coupleA);
        txRepo.Transactions.Add(transaction);
        var goalB = SeedGoal(goalRepo, coupleB);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new LinkTransactionToGoalCommand(transaction.Id, goalB.Id, coupleA),
                CancellationToken.None));
    }
}
