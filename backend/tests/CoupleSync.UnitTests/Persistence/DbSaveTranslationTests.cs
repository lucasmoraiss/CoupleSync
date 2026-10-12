using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CoupleSync.UnitTests.Persistence;

/// <summary>
/// Issue #27: what a save that the database ended (PostgreSQL 40P01, a deadlock; 40001, a serialization failure)
/// becomes for the application. Two requests storing the same things at once is a conflict to answer as such, not an
/// internal error. The provider hands those failures over wrapped ("likely due to a transient failure"), so the whole
/// chain of inner exceptions is read. Runs a real repository whose save fails with the exception of each case.
/// </summary>
public sealed class DbSaveTranslationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DbSaveTranslationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FailingSave(Exception failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw failure;
    }

    private async Task<Exception> SaveFailingWithAsync(Exception failure)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailingSave(failure)).Options;
        await using var db = new AppDbContext(options, coupleContext: null);
        var repository = new IncomeSourceRepository(db);
        return await Assert.ThrowsAnyAsync<Exception>(() => repository.SaveChangesAsync(CancellationToken.None));
    }

    private static PostgresException Postgres(string sqlState) => new("made-up failure", "ERROR", "ERROR", sqlState);

    /// <summary>What the execution strategy of the provider throws around a failure it calls transient.</summary>
    private static InvalidOperationException Wrapped(Exception inner)
        => new("An exception has been raised that is likely due to a transient failure.", inner);

    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public async Task ADeadlockOrASerializationFailure_IsAConcurrencyConflict(string sqlState)
    {
        var failure = new DbUpdateException("An error occurred while saving the entity changes.", Postgres(sqlState));

        var thrown = await SaveFailingWithAsync(failure);

        Assert.IsType<ConcurrencyConflictException>(thrown);
        Assert.Same(failure, thrown.InnerException);
    }

    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public async Task ADeadlockOrASerializationFailure_HandedOverWrapped_IsAConcurrencyConflictToo(string sqlState)
    {
        // Exactly the chain seen on PostgreSQL: InvalidOperationException > DbUpdateException > PostgresException.
        var failure = Wrapped(new DbUpdateException("An error occurred while saving the entity changes.", Postgres(sqlState)));

        var thrown = await SaveFailingWithAsync(failure);

        Assert.IsType<ConcurrencyConflictException>(thrown);
        Assert.Same(failure, thrown.InnerException);

        // And without the save exception in between.
        Assert.IsType<ConcurrencyConflictException>(await SaveFailingWithAsync(Wrapped(Postgres(sqlState))));
    }

    [Fact]
    public async Task TheConflict_SaysWhetherTheDatabaseEndedTheSave_OrSomeoneChangedTheRow()
    {
        // Who catches the conflict to mean "someone else changed this row" can tell the two apart.
        var deadlock = Assert.IsType<ConcurrencyConflictException>(await SaveFailingWithAsync(Wrapped(new DbUpdateException("save", Postgres("40P01")))));
        var serialization = Assert.IsType<ConcurrencyConflictException>(await SaveFailingWithAsync(new DbUpdateException("save", Postgres("40001"))));
        var changed = Assert.IsType<ConcurrencyConflictException>(await SaveFailingWithAsync(new DbUpdateConcurrencyException("someone changed the row")));

        Assert.True(deadlock.EndedByTheDatabase);
        Assert.True(serialization.EndedByTheDatabase);
        Assert.False(changed.EndedByTheDatabase);
    }

    [Fact]
    public async Task AnythingElseHandedOverWrapped_IsNotTranslated()
    {
        // Another failure of the database (a table that is not there), a failure that is not of the database, and
        // an InvalidOperationException of its own: each one leaves exactly as it came.
        var otherSqlState = Wrapped(new DbUpdateException("An error occurred while saving the entity changes.", Postgres("42P01")));
        var notOfTheDatabase = Wrapped(new TimeoutException("made-up timeout"));
        var plain = new InvalidOperationException("made-up misuse");

        Assert.Same(otherSqlState, await SaveFailingWithAsync(otherSqlState));
        Assert.Same(notOfTheDatabase, await SaveFailingWithAsync(notOfTheDatabase));
        Assert.Same(plain, await SaveFailingWithAsync(plain));
    }

    [Fact]
    public async Task WhatWasAlreadyTranslated_IsTranslatedAsBefore()
    {
        Assert.IsType<UniqueViolationException>(await SaveFailingWithAsync(new DbUpdateException("save", Postgres("23505"))));
        Assert.IsType<ForeignKeyViolationException>(await SaveFailingWithAsync(new DbUpdateException("save", Postgres("23503"))));
        Assert.IsType<ConcurrencyConflictException>(await SaveFailingWithAsync(new DbUpdateConcurrencyException("someone changed the row")));
        // Any other failure of a save: the general one, never a conflict.
        Assert.Equal(typeof(DataStoreException), (await SaveFailingWithAsync(new DbUpdateException("save", Postgres("42P01")))).GetType());
        Assert.Equal(typeof(DataStoreException), (await SaveFailingWithAsync(new DbUpdateException("save", new TimeoutException("made-up")))).GetType());
    }
}
