using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.Transactions;

public sealed class UpdateTransactionCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public AlertPolicyTestKit Kit { get; } = new(memberCount: 2, nowUtc: Now);
        public UpdateTransactionCommandHandler Handler { get; }

        public Fixture()
        {
            Handler = new UpdateTransactionCommandHandler(
                Kit.Transactions, new FixedDateTimeProvider(Now), Kit.Service, Kit.Events,
                NullLogger<UpdateTransactionCommandHandler>.Instance);
        }

        public async Task<Transaction> AddAsync(decimal amount, DateTime? at = null, string category = "OUTROS")
        {
            var tx = Transaction.Create(
                Kit.CoupleId, Kit.Author, "imported-fingerprint", "NUBANK", amount, "BRL",
                at ?? Now.AddHours(-2), "Compra", "Loja", category, Guid.NewGuid(), Now, TransactionSource.OcrImport);
            await Kit.Transactions.AddTransactionAsync(tx, CancellationToken.None);
            return tx;
        }
    }

    [Fact]
    public async Task Edit_ChangesTheFields_AndKeepsTheFingerprintOfAnImportedTransaction()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);
        var newDate = Now.AddDays(-3);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, 42.5m, "Nova descrição", newDate, "Saúde"),
            CancellationToken.None);

        Assert.Equal(42.5m, tx.Amount);
        Assert.Equal("Nova descrição", tx.Description);
        Assert.Equal(newDate, tx.EventTimestampUtc);
        Assert.Equal("SAUDE", tx.Category);
        Assert.Equal("imported-fingerprint", tx.Fingerprint);
        Assert.Equal(TransactionSource.OcrImport, tx.Source);
    }

    [Fact]
    public async Task Edit_PartialUpdate_LeavesTheOtherFieldsAlone()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m, category: "LAZER");
        var originalDate = tx.EventTimestampUtc;

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, null, "Só a descrição", null, null),
            CancellationToken.None);

        Assert.Equal(100m, tx.Amount);
        Assert.Equal("LAZER", tx.Category);
        Assert.Equal(originalDate, tx.EventTimestampUtc);
        Assert.Equal("Só a descrição", tx.Description);
    }

    [Fact]
    public async Task Edit_EmptyDescription_ClearsIt()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, null, "  ", null, null), CancellationToken.None);

        Assert.Null(tx.Description);
    }

    [Fact]
    public async Task Edit_TransactionOfAnotherCouple_IsNotFound()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, Guid.NewGuid(), 1m, null, null, null), CancellationToken.None));
        Assert.Equal(100m, tx.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.555)]
    [InlineData(1000000000)]
    public async Task Edit_InvalidAmount_Throws400AndChangesNothing(double amount)
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        var ex = await Assert.ThrowsAsync<AppException>(() => f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, (decimal)amount, null, null, null), CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(100m, tx.Amount);
    }

    [Fact]
    public async Task Edit_InvalidCategory_Throws400()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        var ex = await Assert.ThrowsAsync<AppException>(() => f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, null, null, null, "Inexistente"), CancellationToken.None));

        Assert.Equal("INVALID_CATEGORY", ex.Code);
    }

    [Fact]
    public async Task Edit_RaisingASmallTransactionAboveTheThreshold_RaisesTheLargeTransactionAlert()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, 900m, null, null, null), CancellationToken.None);

        Assert.Contains(f.Kit.Events.Events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task Edit_OfATransactionThatWasAlreadyLarge_DoesNotRepeatTheLargeTransactionAlert()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(900m);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, 1200m, null, null, null), CancellationToken.None);

        Assert.DoesNotContain(f.Kit.Events.Events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task Edit_Merchant_ChangesItAndKeepsTheFingerprint()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, null, null, null, null, "  Loja Nova "), CancellationToken.None);

        Assert.Equal("Loja Nova", tx.Merchant);
        Assert.Equal("imported-fingerprint", tx.Fingerprint);
        Assert.Equal(100m, tx.Amount);
    }

    [Fact]
    public async Task Edit_EmptyMerchant_ClearsIt()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, null, null, null, null, " "), CancellationToken.None);

        Assert.Null(tx.Merchant);
    }

    [Fact]
    public async Task Edit_OfATransactionOutsideTheCurrentBrasiliaMonth_EvaluatesNoAlerts()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(100m);

        // Large amount, but the (new) date is in March: nothing about the current month changes.
        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, 900m, null, new DateTime(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc), null),
            CancellationToken.None);

        Assert.Empty(f.Kit.Events.Events);
    }

    [Fact]
    public async Task Edit_DescriptionOnly_EvaluatesNoAlerts()
    {
        var f = new Fixture();
        var tx = await f.AddAsync(900m);

        await f.Handler.HandleAsync(
            new UpdateTransactionCommand(tx.Id, f.Kit.CoupleId, null, "Outra", null, null), CancellationToken.None);

        Assert.Empty(f.Kit.Events.Events);
    }
}
