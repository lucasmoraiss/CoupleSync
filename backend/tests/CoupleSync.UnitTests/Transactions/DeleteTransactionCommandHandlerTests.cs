using System.Reflection;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.Transactions;

public sealed class DeleteTransactionCommandHandlerTests
{
    private static DeleteTransactionCommandHandler BuildHandler(FakeTransactionRepository? repo = null)
        // No line of the bank points to these transactions (neither as its own nor as a suspected duplicate): the
        // mirror of Open Finance is only asked that, and nothing else of it is touched.
        => new(
            repo ?? new FakeTransactionRepository(),
            NoLinkedLines.Create(),
            new FixedDateTimeProvider(new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc)),
            NullLogger<DeleteTransactionCommandHandler>.Instance);

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

    /// <summary>
    /// A mirror with one line that looks like the transaction, whose reads after the deletion fail
    /// <see cref="Failures"/> times with an error that is not one of the store's (a dropped connection, a timeout).
    /// </summary>
    public class ReadsFailAfterTheDeletion : DispatchProxy
    {
        public int Failures { get; set; }

        public int Reads { get; private set; }

        public int Saves { get; private set; }

        public int Discards { get; private set; }

        public static IBankSyncRepository Create() => Create<IBankSyncRepository, ReadsFailAfterTheDeletion>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IBankSyncRepository.FindAllByLinkedTransactionAsync):
                    return Task.FromResult<IReadOnlyList<BankTransaction>>([]);
                case nameof(IBankSyncRepository.FindWaitingIdsBySuspectAsync):
                    return Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]);
                case nameof(IBankSyncRepository.DiscardChanges):
                    Discards++;
                    return null;
                case nameof(IBankSyncRepository.FindByIdsAsync):
                    Reads++;
                    if (Reads <= Failures) throw new TimeoutException("the read timed out");
                    return Task.FromResult<IReadOnlyList<BankTransaction>>([]);
                case nameof(IBankSyncRepository.GetAccountNamesAsync):
                    return Task.FromResult<IReadOnlyDictionary<Guid, BankAccountNames>>(new Dictionary<Guid, BankAccountNames>());
                case nameof(IBankSyncRepository.SaveChangesAsync):
                    Saves++;
                    return Task.CompletedTask;
            }

            throw new InvalidOperationException($"IBankSyncRepository.{targetMethod?.Name} was not expected to be called.");
        }
    }

    private sealed class CapturingLogger : ILogger<DeleteTransactionCommandHandler>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Assert.Null(exception); // the exception (its text, its stack) is not logged
            var pairs = (IReadOnlyList<KeyValuePair<string, object?>>)state!;
            Warnings.Add((string)pairs.Single(p => p.Key == "ExceptionType").Value!);
        }
    }

    [Theory]
    [InlineData(1, 1)] // a passing failure: the second try judges and stores
    [InlineData(2, 0)] // both tries fail: given up, the deletion stays stored
    [InlineData(5, 0)]
    public async Task HandleAsync_WhenJudgingAgainFailsAfterTheDeletionIsStored_NeverFailsTheDeletion(int failures, int expectedSaves)
    {
        var repo = new FakeTransactionRepository();
        var coupleId = Guid.NewGuid();
        var transaction = BuildTransaction(coupleId);
        await repo.AddTransactionAsync(transaction, CancellationToken.None);
        var mirror = ReadsFailAfterTheDeletion.Create();
        var calls = (ReadsFailAfterTheDeletion)(object)mirror;
        calls.Failures = failures;
        var logger = new CapturingLogger();
        var handler = new DeleteTransactionCommandHandler(
            repo, mirror, new FixedDateTimeProvider(new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc)), logger);

        await handler.HandleAsync(new DeleteTransactionCommand(transaction.Id, Guid.NewGuid(), coupleId), CancellationToken.None);

        Assert.True(repo.DeleteCalled);
        Assert.Equal(2, calls.Reads); // two tries at most, whatever the failure
        Assert.Equal(expectedSaves, calls.Saves);
        Assert.Equal(Math.Min(failures, 2), calls.Discards);
        // Given up (both tries failed) is told once, by the kind of error alone: never its text.
        var expectedWarnings = expectedSaves == 0 ? new[] { nameof(TimeoutException) } : [];
        Assert.Equal(expectedWarnings, logger.Warnings);
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
            repo, mirror, new FixedDateTimeProvider(new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc)),
            NullLogger<DeleteTransactionCommandHandler>.Instance);

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
