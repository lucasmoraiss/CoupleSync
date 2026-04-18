using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Transactions;

public sealed class UpdateTransactionCategoryCommandHandlerTests
{
    private static UpdateTransactionCategoryCommandHandler BuildHandler(FakeTransactionRepository? repo = null)
        => new(repo ?? new FakeTransactionRepository());

    private static Transaction BuildTransaction(Guid coupleId, string category = "OUTROS")
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
            category,
            Guid.NewGuid(),
            DateTime.UtcNow);
    }

    [Fact]
    public async Task HandleAsync_WithValidTransaction_UpdatesCategoryAndReturnsResult()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var transaction = BuildTransaction(coupleId, "OUTROS");
        await repo.AddTransactionAsync(transaction, CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new UpdateTransactionCategoryCommand(transaction.Id, coupleId, "Alimentação"),
            CancellationToken.None);

        Assert.Equal(transaction.Id, result.Id);
        Assert.Equal("Alimentação", result.Category);
        Assert.Equal("Alimentação", transaction.Category);
    }

    [Fact]
    public async Task HandleAsync_WhenTransactionNotFound_ThrowsNotFoundException()
    {
        var handler = BuildHandler();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new UpdateTransactionCategoryCommand(Guid.NewGuid(), Guid.NewGuid(), "Alimentação"),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_UpdateCategoryResult_ContainsAllTransactionFields()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var ingestId = Guid.NewGuid();

        var transaction = Transaction.Create(
            coupleId, userId, "fp-fields", "ITAU", 500m, "BRL",
            timestamp, "Grocery", "Carrefour", "OUTROS", ingestId, DateTime.UtcNow);

        await repo.AddTransactionAsync(transaction, CancellationToken.None);
        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new UpdateTransactionCategoryCommand(transaction.Id, coupleId, "Mercado"),
            CancellationToken.None);

        Assert.Equal(transaction.Id, result.Id);
        Assert.Equal(coupleId, result.CoupleId);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("ITAU", result.Bank);
        Assert.Equal(500m, result.Amount);
        Assert.Equal("BRL", result.Currency);
        Assert.Equal(timestamp, result.EventTimestampUtc);
        Assert.Equal("Grocery", result.Description);
        Assert.Equal("Carrefour", result.Merchant);
        Assert.Equal("Mercado", result.Category);
    }

    [Fact]
    public async Task HandleAsync_CategoryCanBeUpdatedMultipleTimes()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var transaction = BuildTransaction(coupleId, "OUTROS");
        await repo.AddTransactionAsync(transaction, CancellationToken.None);

        var handler = BuildHandler(repo);

        await handler.HandleAsync(
            new UpdateTransactionCategoryCommand(transaction.Id, coupleId, "Alimentação"),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new UpdateTransactionCategoryCommand(transaction.Id, coupleId, "Transporte"),
            CancellationToken.None);

        Assert.Equal("Transporte", result.Category);
        Assert.Equal("Transporte", transaction.Category);
    }

    [Fact]
    public async Task HandleAsync_TransactionFromAnotherCouple_ThrowsNotFoundException()
    {
        var repo = new FakeTransactionRepository();
        var coupleIdA = Guid.NewGuid();
        var coupleIdB = Guid.NewGuid();
        var transaction = BuildTransaction(coupleIdA, "OUTROS");
        await repo.AddTransactionAsync(transaction, CancellationToken.None);

        var handler = BuildHandler(repo);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(
                new UpdateTransactionCategoryCommand(transaction.Id, coupleIdB, "Alimentação"),
                CancellationToken.None));
    }
}
