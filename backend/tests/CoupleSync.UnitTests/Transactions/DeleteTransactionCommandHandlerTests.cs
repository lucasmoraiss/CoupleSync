using System.Reflection;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Transactions;

public sealed class DeleteTransactionCommandHandlerTests
{
    private static DeleteTransactionCommandHandler BuildHandler(FakeTransactionRepository? repo = null)
        // No line of the bank points to these transactions (neither as its own nor as a suspected duplicate): the
        // mirror of Open Finance is only asked that, and nothing else of it is touched.
        => new(
            repo ?? new FakeTransactionRepository(),
            NoLinkedLines.Create(),
            new FixedDateTimeProvider(new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc)));

    /// <summary>The mirror answers "no line points to it" and refuses any other call.</summary>
    public class NoLinkedLines : DispatchProxy
    {
        public static IBankSyncRepository Create() => Create<IBankSyncRepository, NoLinkedLines>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                nameof(IBankSyncRepository.FindAllByLinkedTransactionAsync) => (object)Task.FromResult<IReadOnlyList<BankTransaction>>([]),
                nameof(IBankSyncRepository.FindWaitingIdsBySuspectAsync) => Task.FromResult<IReadOnlyList<Guid>>([]),
                _ => throw new InvalidOperationException($"IBankSyncRepository.{targetMethod?.Name} was not expected to be called."),
            };
    }

    /// <summary>
    /// A mirror with one line that looks like the transaction. The request is given up right after the deletion is
    /// asked for; from then on every call refuses a cancelled token, as the real store does.
    /// </summary>
    public class OneLineLooksLikeIt : DispatchProxy
    {
        public CancellationTokenSource Request { get; } = new();

        public List<string> CalledAfterTheDeletion { get; } = [];

        public static IBankSyncRepository Create() => Create<IBankSyncRepository, OneLineLooksLikeIt>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IBankSyncRepository.FindAllByLinkedTransactionAsync):
                    return Task.FromResult<IReadOnlyList<BankTransaction>>([]);
                case nameof(IBankSyncRepository.FindWaitingIdsBySuspectAsync):
                    // The last thing read before the deletion is stored.
                    Request.Cancel();
                    return Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]);
                case nameof(IBankSyncRepository.DiscardChanges):
                    return null;
            }

            CalledAfterTheDeletion.Add(targetMethod!.Name);
            if (((CancellationToken)args![^1]!).IsCancellationRequested)
                return targetMethod.Name switch
                {
                    nameof(IBankSyncRepository.FindByIdsAsync) => Task.FromCanceled<IReadOnlyList<BankTransaction>>(Request.Token),
                    nameof(IBankSyncRepository.GetAccountNamesAsync) => Task.FromCanceled<IReadOnlyDictionary<Guid, BankAccountNames>>(Request.Token),
                    _ => Task.FromCanceled(Request.Token),
                };

            return targetMethod.Name switch
            {
                // The line was answered meanwhile: nothing is left waiting to judge, and the save stores nothing.
                nameof(IBankSyncRepository.FindByIdsAsync) => Task.FromResult<IReadOnlyList<BankTransaction>>([]),
                nameof(IBankSyncRepository.GetAccountNamesAsync) => Task.FromResult<IReadOnlyDictionary<Guid, BankAccountNames>>(new Dictionary<Guid, BankAccountNames>()),
                nameof(IBankSyncRepository.SaveChangesAsync) => Task.CompletedTask,
                _ => throw new InvalidOperationException($"IBankSyncRepository.{targetMethod.Name} was not expected to be called."),
            };
        }
    }

    [Fact]
    public async Task HandleAsync_WhenTheRequestIsGivenUpAfterTheDeletionIsStored_StillJudgesAgainTheLinesThatLookedLikeIt()
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var transaction = BuildTransaction(coupleId);
        await repo.AddTransactionAsync(transaction, CancellationToken.None);
        var mirror = OneLineLooksLikeIt.Create();
        var calls = (OneLineLooksLikeIt)(object)mirror;
        var handler = new DeleteTransactionCommandHandler(
            repo, mirror, new FixedDateTimeProvider(new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc)));

        await handler.HandleAsync(new DeleteTransactionCommand(transaction.Id, Guid.NewGuid(), coupleId), calls.Request.Token);

        Assert.True(calls.Request.IsCancellationRequested);
        Assert.True(repo.DeleteCalled);
        Assert.Equal(
            [nameof(IBankSyncRepository.FindByIdsAsync), nameof(IBankSyncRepository.GetAccountNamesAsync), nameof(IBankSyncRepository.SaveChangesAsync)],
            calls.CalledAfterTheDeletion);
    }

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
