using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Persistence;

/// <summary>
/// Runs, on SQLite, the statements the AddCoupleOwnerAndJoinCodeExpiry migration sends to PostgreSQL
/// against groups that already existed before the owner and expiry columns.
/// </summary>
public sealed class CoupleGroupManagementSqlTests : IDisposable
{
    private static readonly DateTime MigrationTime = new(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CoupleGroupManagementSqlTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options, coupleContext: null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static string G(int n) => $"00000000-0000-0000-0000-{n:D12}";

    // A pre-migration row: no owner, the expiry column still holding the migration default.
    private void SeedLegacyGroup(int coupleN, string code)
    {
        Exec($"INSERT INTO couples (id, join_code, status, created_at, join_code_expires_at_utc, owner_user_id) " +
             $"VALUES ('{G(coupleN)}', '{code}', 'Active', '2026-01-01 00:00:00', '0001-01-01 00:00:00', NULL)");
    }

    private void SeedUser(int n, int coupleN, string created, string? joined)
    {
        var joinedSql = joined is null ? "NULL" : $"'{joined}'";
        Exec($"INSERT INTO users (id, email, name, password_hash, created_at_utc, is_active, couple_id, couple_joined_at_utc) " +
             $"VALUES ('{G(n)}', 'u{n}@example.com', 'U{n}', 'h', '{created}', 1, '{G(coupleN)}', {joinedSql})");
    }

    [Fact]
    public void OldestMemberBecomesOwner_UsingJoinDateThenCreationDateThenId()
    {
        // Group 1: user 12 joined first even though user 11 registered earlier.
        SeedLegacyGroup(1, "AAAAAA");
        SeedUser(11, 1, "2026-01-01 00:00:00", "2026-03-01 00:00:00");
        SeedUser(12, 1, "2026-02-01 00:00:00", "2026-02-15 00:00:00");
        // Group 2: no join dates recorded, so account creation decides; the tie goes to the lowest id.
        SeedLegacyGroup(2, "BBBBBB");
        SeedUser(21, 2, "2026-01-05 00:00:00", null);
        SeedUser(23, 2, "2026-01-02 00:00:00", null);
        SeedUser(22, 2, "2026-01-02 00:00:00", null);
        // Group 3 has nobody.
        SeedLegacyGroup(3, "CCCCCC");
        // A group that already has an owner keeps it.
        SeedLegacyGroup(4, "DDDDDD");
        SeedUser(41, 4, "2026-01-01 00:00:00", "2026-01-01 00:00:00");
        SeedUser(42, 4, "2026-01-02 00:00:00", "2026-01-02 00:00:00");
        Exec($"UPDATE couples SET owner_user_id = '{G(42)}' WHERE id = '{G(4)}'");

        Exec(CoupleGroupManagementSql.SetOldestMemberAsOwner);

        Assert.Equal(G(12), Scalar($"SELECT owner_user_id FROM couples WHERE id = '{G(1)}'"));
        Assert.Equal(G(22), Scalar($"SELECT owner_user_id FROM couples WHERE id = '{G(2)}'"));
        Assert.Null(Scalar($"SELECT owner_user_id FROM couples WHERE id = '{G(3)}'"));
        Assert.Equal(G(42), Scalar($"SELECT owner_user_id FROM couples WHERE id = '{G(4)}'"));
    }

    [Fact]
    public void ExistingCodesGetSevenDaysFromMigrationTime_AndEntityReadsThemBack()
    {
        SeedLegacyGroup(1, "AAAAAA");
        SeedLegacyGroup(2, "BBBBBB");

        Exec(CoupleGroupManagementSql.GiveExistingCodesSevenDays(MigrationTime));

        using var ctx = new AppDbContext(_options, coupleContext: null);
        var groups = ctx.Couples.AsNoTracking().ToList();
        Assert.Equal(2, groups.Count);
        // SQLite hands a "...Z" text back as local time; the instant is what matters (PostgreSQL stores a timestamptz).
        Assert.All(groups, g => Assert.Equal(MigrationTime.AddDays(7), g.JoinCodeExpiresAtUtc.ToUniversalTime()));
    }
}
