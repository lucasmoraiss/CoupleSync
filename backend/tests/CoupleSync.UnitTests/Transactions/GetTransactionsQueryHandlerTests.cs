using CoupleSync.Application.Transactions.Queries;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Transactions;

public sealed class GetTransactionsQueryHandlerTests
{
    private static GetTransactionsQueryHandler BuildHandler(FakeTransactionRepository? repo = null)
        => new(repo ?? new FakeTransactionRepository());

    private static Transaction BuildTransaction(
        Guid coupleId,
        string category = "Alimentação",
        DateTime? eventTimestamp = null)
    {
        return Transaction.Create(
            coupleId,
            Guid.NewGuid(),
            $"fp-{Guid.NewGuid():N}",
            "NUBANK",
            100m,
            "BRL",
            eventTimestamp ?? new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc),
            "Test purchase",
            "Test Store",
            category,
            Guid.NewGuid(),
            DateTime.UtcNow);
    }

    [Fact]
    public async Task HandleAsync_WithNoTransactions_ReturnsEmptyPage()
    {
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 1, 20, null, null, null),
            CancellationToken.None);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
    }

    [Fact]
    public async Task HandleAsync_WithTransactions_ReturnsOnlyCoupleTransactions()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var otherCoupleId = Guid.NewGuid();

        await repo.AddTransactionAsync(BuildTransaction(coupleId), CancellationToken.None);
        await repo.AddTransactionAsync(BuildTransaction(coupleId), CancellationToken.None);
        await repo.AddTransactionAsync(BuildTransaction(otherCoupleId), CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 1, 20, null, null, null),
            CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal(coupleId, item.CoupleId));
    }

    [Fact]
    public async Task HandleAsync_WithCategoryFilter_ReturnsOnlyMatchingCategory()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();

        await repo.AddTransactionAsync(BuildTransaction(coupleId, "Alimentação"), CancellationToken.None);
        await repo.AddTransactionAsync(BuildTransaction(coupleId, "Transporte"), CancellationToken.None);
        await repo.AddTransactionAsync(BuildTransaction(coupleId, "Alimentação"), CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 1, 20, "Alimentação", null, null),
            CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal("Alimentação", item.Category));
    }

    [Fact]
    public async Task HandleAsync_WithDateFilters_ReturnsOnlyTransactionsInRange()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 10, 23, 59, 59, DateTimeKind.Utc);

        await repo.AddTransactionAsync(BuildTransaction(coupleId, eventTimestamp: new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc)), CancellationToken.None);
        await repo.AddTransactionAsync(BuildTransaction(coupleId, eventTimestamp: new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc)), CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 1, 20, null, start, end),
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        Assert.All(result.Items, item =>
        {
            Assert.True(item.EventTimestampUtc >= start);
            Assert.True(item.EventTimestampUtc <= end);
        });
    }

    [Fact]
    public async Task HandleAsync_PaginationFirstPage_ReturnsCorrectSubset()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            await repo.AddTransactionAsync(BuildTransaction(coupleId), CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 1, 2, null, null, null),
            CancellationToken.None);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, result.Page);
        Assert.Equal(2, result.PageSize);
    }

    [Fact]
    public async Task HandleAsync_PaginationSecondPage_ReturnsNextSubset()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            await repo.AddTransactionAsync(BuildTransaction(coupleId), CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 2, 2, null, null, null),
            CancellationToken.None);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Page);
    }

    [Fact]
    public async Task HandleAsync_PaginationBeyondLastPage_ReturnsEmpty()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();

        await repo.AddTransactionAsync(BuildTransaction(coupleId), CancellationToken.None);

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 5, 20, null, null, null),
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task HandleAsync_MapsTransactionFieldsCorrectly()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);

        var transaction = Transaction.Create(
            coupleId, userId, "fp-test", "NUBANK", 250.50m, "BRL",
            timestamp, "Coffee", "Starbucks", "Alimentação", Guid.NewGuid(), DateTime.UtcNow);

        await repo.AddTransactionAsync(transaction, CancellationToken.None);
        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetTransactionsQuery(coupleId, 1, 20, null, null, null),
            CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(coupleId, item.CoupleId);
        Assert.Equal(userId, item.UserId);
        Assert.Equal("NUBANK", item.Bank);
        Assert.Equal(250.50m, item.Amount);
        Assert.Equal("BRL", item.Currency);
        Assert.Equal(timestamp, item.EventTimestampUtc);
        Assert.Equal("Coffee", item.Description);
        Assert.Equal("Starbucks", item.Merchant);
        Assert.Equal("Alimentação", item.Category);
    }
}
