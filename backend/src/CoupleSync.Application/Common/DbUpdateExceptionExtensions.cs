using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Common;

public static class DbUpdateExceptionExtensions
{
    /// <summary>
    /// True when a save failed on a unique index (PostgreSQL 23505, or SQLite's "UNIQUE constraint failed").
    /// The one detector the Application layer uses to turn a lost insert race into a retry or a conflict.
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.Ordinal)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}
