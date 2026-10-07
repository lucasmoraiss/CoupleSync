namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// SQL of the AddCoupleMembers migration: turns the single group of each existing user (users.couple_id) into a
/// row of couple_members. PostgreSQL only (production); proven by the legacy-data test in CoupleSync.PostgresTests.
/// Nothing is deleted and users.couple_id is left as it is (it becomes the user's active group).
/// </summary>
public static class CoupleMembersMigrationSql
{
    /// <summary>
    /// One membership per user that has a group; joined when the user joined it (the account's creation date
    /// when that was never recorded). Everyone starts as a member; the owner is marked by the next statements.
    /// </summary>
    public const string BackfillMemberships = """
        INSERT INTO couple_members (couple_id, user_id, role, joined_at_utc)
        SELECT u.couple_id, u.id, 'Member', COALESCE(u.couple_joined_at_utc, u.created_at_utc)
        FROM users AS u
        WHERE u.couple_id IS NOT NULL
        """;

    /// <summary>The registered owner of each group (couples.owner_user_id), when that user is one of its members.</summary>
    public const string MarkRegisteredOwners = """
        UPDATE couple_members
        SET role = 'Owner'
        WHERE EXISTS (
            SELECT 1
            FROM couples AS c
            WHERE c.id = couple_members.couple_id AND c.owner_user_id = couple_members.user_id
        )
        """;

    /// <summary>
    /// A group that has members but whose registered owner is missing or is not one of them (possible after two
    /// members left at the same moment, before these changes were serialised) gets its oldest member as owner.
    /// </summary>
    public const string GiveOwnerlessGroupsTheirOldestMember = """
        UPDATE couple_members
        SET role = 'Owner'
        WHERE NOT EXISTS (
                SELECT 1
                FROM couple_members AS o
                WHERE o.couple_id = couple_members.couple_id AND o.role = 'Owner'
            )
            AND user_id = (
                SELECT m.user_id
                FROM couple_members AS m
                WHERE m.couple_id = couple_members.couple_id
                ORDER BY m.joined_at_utc ASC, m.user_id ASC
                LIMIT 1
            )
        """;

    /// <summary>couples.owner_user_id says the same as the owner row: that member, or nobody for a group without members.</summary>
    public const string AlignRegisteredOwnerWithOwnerRow = """
        UPDATE couples
        SET owner_user_id = (
            SELECT m.user_id
            FROM couple_members AS m
            WHERE m.couple_id = couples.id AND m.role = 'Owner'
        )
        WHERE owner_user_id IS DISTINCT FROM (
            SELECT m.user_id
            FROM couple_members AS m
            WHERE m.couple_id = couples.id AND m.role = 'Owner'
        )
        """;

    /// <summary>A group nobody belongs to must not be joinable: its invite code expires now (its data is kept).</summary>
    public const string ExpireCodesOfGroupsWithoutMembers = """
        UPDATE couples
        SET join_code_expires_at_utc = now()
        WHERE join_code_expires_at_utc > now()
            AND NOT EXISTS (SELECT 1 FROM couple_members AS m WHERE m.couple_id = couples.id)
        """;

    /// <summary>
    /// For the way back only: the previous schema allows one row of alert preferences per user. The row of the
    /// user's active group (users.couple_id, the only group left to them) is the one that stays.
    /// </summary>
    public const string KeepNotificationSettingsOfTheActiveGroup = """
        DELETE FROM notification_settings AS ns
        USING users AS u
        WHERE u.id = ns.user_id
            AND ns.couple_id IS DISTINCT FROM u.couple_id
            AND EXISTS (
                SELECT 1
                FROM notification_settings AS active
                WHERE active.user_id = ns.user_id AND active.couple_id = u.couple_id
            )
        """;

    /// <summary>For the way back only: of what is still duplicated per user, the most recently changed row stays.</summary>
    public const string KeepOneNotificationSettingsRowPerUser = """
        DELETE FROM notification_settings AS ns
        USING notification_settings AS other
        WHERE ns.user_id = other.user_id
            AND (ns.updated_at_utc, ns.id) < (other.updated_at_utc, other.id)
        """;
}
