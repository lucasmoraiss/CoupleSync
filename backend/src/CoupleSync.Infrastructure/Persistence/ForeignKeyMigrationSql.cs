namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// SQL used by the AddForeignKeysForCoupleAndUserRelations migration (PostgreSQL only: NOT VALID and DO blocks).
/// Tables that only hold derived data (device tokens, notification settings and events) lose orphan rows before
/// the key is created. Tables that hold the users' own data are never touched: their keys are created NOT VALID
/// (enforced for every new or changed row, existing rows are not checked) and then validated when no orphan
/// exists. A table that does have orphans keeps the key NOT VALID and the migration logs a WARNING naming it.
/// </summary>
public static class ForeignKeyMigrationSql
{
    /// <summary>Derived-data tables: (table, column, principal table).</summary>
    public static readonly (string Table, string Column, string Principal)[] DerivedKeys =
    [
        ("device_tokens", "couple_id", "couples"),
        ("device_tokens", "user_id", "users"),
        ("notification_settings", "couple_id", "couples"),
        ("notification_settings", "user_id", "users"),
        ("notification_events", "couple_id", "couples"),
        ("notification_events", "user_id", "users"),
    ];

    /// <summary>User-data tables: (table, column, principal table).</summary>
    public static readonly (string Table, string Column, string Principal)[] UserDataKeys =
    [
        ("transactions", "couple_id", "couples"),
        ("transactions", "user_id", "users"),
        ("transaction_event_ingests", "couple_id", "couples"),
        ("transaction_event_ingests", "user_id", "users"),
        ("goals", "couple_id", "couples"),
        ("goals", "created_by_user_id", "users"),
        ("income_sources", "user_id", "users"),
        ("import_jobs", "couple_id", "couples"),
        ("import_jobs", "user_id", "users"),
    ];

    /// <summary>Applies to the migration's own transaction only (SET LOCAL).</summary>
    public const string SetShortLockTimeout = "SET LOCAL lock_timeout = '5s'";

    public static string ConstraintName(string table, string column, string principal) =>
        $"FK_{table}_{principal}_{column}";

    /// <summary>Removes the rows of a derived-data table whose column points to a row that does not exist.</summary>
    public static string DeleteOrphans(string table, string column, string principal) =>
        $"DELETE FROM {table} AS t WHERE NOT EXISTS (SELECT 1 FROM {principal} AS p WHERE p.id = t.{column})";

    /// <summary>Creates the key without checking existing rows, then validates it if every row is consistent.</summary>
    public static string AddNotValidThenValidateIfPossible(string table, string column, string principal)
    {
        var name = ConstraintName(table, column, principal);
        return $"""
            ALTER TABLE {table}
                ADD CONSTRAINT "{name}" FOREIGN KEY ({column}) REFERENCES {principal} (id) ON DELETE RESTRICT NOT VALID;
            DO $fk$
            BEGIN
                ALTER TABLE {table} VALIDATE CONSTRAINT "{name}";
            EXCEPTION WHEN foreign_key_violation THEN
                RAISE WARNING 'Foreign key {name} left NOT VALID: {table}.{column} has rows pointing to a missing {principal} row. The rows were kept; new rows are still checked.';
            END
            $fk$;
            """;
    }
}
