using CoupleSync.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// Saves the context and translates the provider's failures into the application's typed exceptions, so the
/// Application layer never sees EF Core types. The original exception stays as the inner exception.
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
    }

    public static DataStoreException Translate(DbUpdateException ex)
    {
        if (ex is DbUpdateConcurrencyException)
            return new ConcurrencyConflictException(ex.Message, ex);

        if (IsUniqueViolation(ex))
            return new UniqueViolationException(ex.Message, ex);

        if (IsForeignKeyViolation(ex))
            return new ForeignKeyViolationException(ex.Message, ex);

        return new DataStoreException(ex.Message, ex);
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
