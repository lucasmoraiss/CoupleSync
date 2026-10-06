namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// SQL used by the DeviceTokenUniquePerToken migration, before it creates the unique index on token.
/// Plain DELETE with a correlated EXISTS and row comparisons, valid on PostgreSQL (production) and SQLite
/// (unit tests), so the statement that runs in production is exercised by tests.
/// </summary>
public static class DeviceTokenDeduplicationSql
{
    /// <summary>
    /// Keeps, for each token, only the newest row: the latest last_seen_at_utc, then the latest
    /// created_at_utc, then the highest id (so exactly one row survives even on a full tie).
    /// </summary>
    public const string KeepNewestRowPerToken = """
        DELETE FROM device_tokens
        WHERE EXISTS (
            SELECT 1
            FROM device_tokens AS newer
            WHERE newer.token = device_tokens.token
              AND (
                    newer.last_seen_at_utc > device_tokens.last_seen_at_utc
                 OR (newer.last_seen_at_utc = device_tokens.last_seen_at_utc
                     AND newer.created_at_utc > device_tokens.created_at_utc)
                 OR (newer.last_seen_at_utc = device_tokens.last_seen_at_utc
                     AND newer.created_at_utc = device_tokens.created_at_utc
                     AND newer.id > device_tokens.id)
              )
        )
        """;
}
