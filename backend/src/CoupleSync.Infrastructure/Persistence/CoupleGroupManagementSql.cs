using System.Globalization;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// SQL used by the AddCoupleOwnerAndJoinCodeExpiry migration to fill the new columns of groups that already
/// exist. Plain UPDATEs with a correlated subquery, valid on PostgreSQL (production) and SQLite (unit tests),
/// so the statements that run in production are exercised by tests.
/// </summary>
public static class CoupleGroupManagementSql
{
    /// <summary>
    /// A group with no registered owner gets its oldest member: earliest couple_joined_at_utc (the user's creation
    /// date when that is unknown), then the lowest id. Groups without members stay without an owner.
    /// </summary>
    public const string SetOldestMemberAsOwner = """
        UPDATE couples
        SET owner_user_id = (
            SELECT u.id
            FROM users AS u
            WHERE u.couple_id = couples.id
            ORDER BY COALESCE(u.couple_joined_at_utc, u.created_at_utc) ASC, u.id ASC
            LIMIT 1
        )
        WHERE owner_user_id IS NULL
        """;

    /// <summary>Every existing invite code stays valid for 7 days from the moment the migration runs.</summary>
    public static string GiveExistingCodesSevenDays(DateTime migrationTimeUtc)
    {
        var expiresAt = migrationTimeUtc.ToUniversalTime().Add(TimeSpan.FromDays(7))
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return $"UPDATE couples SET join_code_expires_at_utc = '{expiresAt}'";
    }
}
