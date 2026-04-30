using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Transactions;

public sealed class DeleteTransactionCommandHandlerTests
{
    private static DeleteTransactionCommandHandler BuildHandler(FakeTransactionRepository? repo = null)
        => new(repo ?? new FakeTransactionRepository());

    private static Transaction BuildTransaction(Guid coupleId)
    {
        return Transaction.Create(
            coupleId,
            Guid.NewGuid(),
            $"fp-{Guid.NewGuid():N}",
            "NUBANK",
            250m,
            "BRL",
            new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc),
            "Test purchase",
            "Test Store",
            "OUTROS",
            Guid.NewGuid(),
            DateTime.UtcNow);
    }

    [Fact]
    public async Task HandleAsync_WithValidTransaction_DeletesAndReturnsSuccessfully()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var transaction = BuildTransaction(coupleId);
        await repo.AddTransactionAsync(transaction, CancellationToken.None);

        var handler = BuildHandler(repo);

        await handler.HandleAsync(
            new DeleteTransactionCommand(transaction.Id, userId, coupleId),
            CancellationToken.None);

        Assert.True(repo.DeleteCalled);
        Assert.DoesNotContain(transaction, repo.Transactions);
    }

    [Fact]
    public async Task HandleAsync_WhenTransactionNotFound_ThrowsNotFoundException()
    {
        var handler = BuildHandler();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new DeleteTransactionCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTransactionBelongsToForeignCouple_ThrowsForbiddenException()
    {
        var repo = new FakeTransactionRepository();
        var ownerCoupleId = Guid.NewGuid();
        var foreignCoupleId = Guid.NewGuid();
        var transaction = BuildTransaction(ownerCoupleId);
        await repo.AddTransactionAsync(transaction, CancellationToken.None);

        var handler = BuildHandler(repo);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            handler.HandleAsync(
                new DeleteTransactionCommand(transaction.Id, Guid.NewGuid(), foreignCoupleId),
                CancellationToken.None));
    }
}
