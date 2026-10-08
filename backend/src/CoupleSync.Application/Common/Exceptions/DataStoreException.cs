namespace CoupleSync.Application.Common.Exceptions;

/// <summary>
/// A write to the data store failed. Raised by the repositories' save operations (the original provider exception is the
/// inner exception); the subclasses name the failures that handlers react to. Not an <see cref="AppException"/>: an
/// unhandled one is still an internal error (500).
/// </summary>
public class DataStoreException : Exception
{
    public DataStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The write lost a race on a unique index (another request stored the same key first).</summary>
public sealed class UniqueViolationException : DataStoreException
{
    public UniqueViolationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The write referred to a row that is not there (another request deleted it first), or deleted a row that others
/// still refer to. Nothing of the failed write was stored.
/// </summary>
public sealed class ForeignKeyViolationException : DataStoreException
{
    public ForeignKeyViolationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The write touched a row that another request changed or deleted in the meantime (optimistic concurrency).
/// Nothing of the failed write was stored.
/// </summary>
public sealed class ConcurrencyConflictException : DataStoreException
{
    public ConcurrencyConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
