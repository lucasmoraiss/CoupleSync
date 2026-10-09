using CoupleSync.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// Saves the context and translates the provider's failures into the application's typed exceptions, so the
/// Application layer never sees EF Core types. The original exception stays as the inner exception.
/// A save PostgreSQL ended because two requests were in each other's way (a deadlock, 40P01, or a serialization
/// failure, 40001) is a concurrency conflict like any other: nothing of it was stored. The provider hands those two
/// over wrapped in an <see cref="InvalidOperationException"/> ("likely due to a transient failure", from its
/// execution strategy), so they are looked for in the whole chain of inner exceptions; any other
/// <see cref="InvalidOperationException"/> leaves untouched.
/// </summary>
internal static class DbSaveTranslator
{
    public static async Task SaveAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw Translate(ex);
        }
        catch (InvalidOperationException ex) when (IsDeadlockOrSerializationFailure(ex))
        {
            throw new ConcurrencyConflictException(ex.Message, ex);
        }
    }

    /// <summary>
    /// As <see cref="SaveAsync(DbContext, CancellationToken)"/>, but a save refused by a concurrency token is first
    /// handed to <paramref name="resolve"/>: when it answers true (it brought the tracked rows up to date), the save
    /// is tried again; when it answers false, the refusal is translated as any other.
    /// </summary>
    public static async Task SaveAsync(
        DbContext dbContext, Func<DbUpdateConcurrencyException, CancellationToken, Task<bool>> resolve, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (!await resolve(ex, cancellationToken)) throw Translate(ex);
            }
            catch (DbUpdateException ex)
            {
                throw Translate(ex);
            }
            catch (InvalidOperationException ex) when (IsDeadlockOrSerializationFailure(ex))
            {
                throw new ConcurrencyConflictException(ex.Message, ex);
            }
        }
    }

    public static DataStoreException Translate(DbUpdateException ex)
    {
        if (ex is DbUpdateConcurrencyException)
            return new ConcurrencyConflictException(ex.Message, ex);

        if (IsUniqueViolation(ex))
            return new UniqueViolationException(ex.Message, ex);

        if (IsForeignKeyViolation(ex))
            return new ForeignKeyViolationException(ex.Message, ex);

        if (IsDeadlockOrSerializationFailure(ex))
            return new ConcurrencyConflictException(ex.Message, ex);

        return new DataStoreException(ex.Message, ex);
    }

    /// <summary>
    /// True when, anywhere in the chain of <paramref name="ex"/>, PostgreSQL says it ended the transaction because
    /// of another one running at the same time: a deadlock (40P01) or a serialization failure (40001).
    /// </summary>
    private static bool IsDeadlockOrSerializationFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                return true;
        }

        return false;
    }

    /// <summary>True when a save failed on a foreign key (PostgreSQL 23503, or SQLite's "FOREIGN KEY constraint failed").</summary>
    private static bool IsForeignKeyViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23503", StringComparison.Ordinal)
            || message.Contains("foreign key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a save failed on a unique index (PostgreSQL 23505, or SQLite's "UNIQUE constraint failed").</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.Ordinal)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}
