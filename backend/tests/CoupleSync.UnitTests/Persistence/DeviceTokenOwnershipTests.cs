using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Persistence;

/// <summary>
/// S3 3.92: a device token belongs to a single user. Runs the real repository against SQLite (unique indexes
/// included) and the very SQL the DeviceTokenUniquePerToken migration sends to PostgreSQL.
/// </summary>
public sealed class DeviceTokenOwnershipTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly DeviceTokenRepository _repository;

    public DeviceTokenOwnershipTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        // These tests are about token ownership with made-up ids; the user/group keys are covered on PostgreSQL.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            pragma.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options, coupleContext: null);
        _db.Database.EnsureCreated();
        _repository = new DeviceTokenRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task RegisterAsync(Guid user, Guid couple, string token, DateTime now)
    {
        await _repository.UpsertAsync(user, couple, token, now, CancellationToken.None);
        await _repository.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
    }

    private Task<List<DeviceToken>> AllAsync() => _db.DeviceTokens.IgnoreQueryFilters().AsNoTracking().ToListAsync();

    // -- repository -----------------------------------------------------------

    [Fact]
    public async Task Register_TokenAlreadyHeldByAnotherUser_MovesItToTheCurrentUser()
    {
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        var previousOwner = Guid.NewGuid();
        var newOwner = Guid.NewGuid();
        await RegisterAsync(previousOwner, coupleA, "device-1", T0);

        await RegisterAsync(newOwner, coupleB, "device-1", T0.AddHours(1));

        var row = Assert.Single(await AllAsync());
        Assert.Equal(newOwner, row.UserId);
        Assert.Equal(coupleB, row.CoupleId);
        Assert.Equal("device-1", row.Token);
        Assert.Empty(await _repository.GetByUserIdAsync(previousOwner, CancellationToken.None));
    }

    [Fact]
    public async Task Register_NewOwnerWithAnOlderTokenOfItsOwn_ReplacesIt_AndLeavesNoDuplicate()
    {
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        var previousOwner = Guid.NewGuid();
        var newOwner = Guid.NewGuid();
        await RegisterAsync(previousOwner, coupleA, "device-1", T0);
        await RegisterAsync(newOwner, coupleB, "device-old", T0);

        await RegisterAsync(newOwner, coupleB, "device-1", T0.AddHours(1));

        var row = Assert.Single(await AllAsync());
        Assert.Equal(newOwner, row.UserId);
        Assert.Equal("device-1", row.Token);
    }

    [Fact]
    public async Task Register_SameUserTwice_StaysASingleRow_AndFollowsTheNewCouple()
    {
        var user = Guid.NewGuid();
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();
        await RegisterAsync(user, coupleA, "device-1", T0);

        await RegisterAsync(user, coupleB, "device-2", T0.AddDays(1));

        var row = Assert.Single(await AllAsync());
        Assert.Equal("device-2", row.Token);
        Assert.Equal(coupleB, row.CoupleId);
    }

    [Fact]
    public async Task Register_DifferentTokensOfDifferentUsers_AreBothKept()
    {
        var couple = Guid.NewGuid();
        await RegisterAsync(Guid.NewGuid(), couple, "device-1", T0);
        await RegisterAsync(Guid.NewGuid(), couple, "device-2", T0);

        Assert.Equal(2, (await AllAsync()).Count);
    }

    // -- migration SQL --------------------------------------------------------

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private void InsertLegacyRow(string id, Guid user, string token, string lastSeen, string created)
        => Execute(
            "INSERT INTO device_tokens (id, user_id, couple_id, token, platform, last_seen_at_utc, created_at_utc) " +
            "VALUES (@id, @user, @couple, @token, @platform, @lastSeen, @created)",
            ("@id", Guid.Parse(id)), ("@user", user), ("@couple", Guid.NewGuid()), ("@token", token),
            ("@platform", "android-" + id), ("@lastSeen", lastSeen), ("@created", created));

    private List<string> SurvivingIds()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM device_tokens ORDER BY id";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetGuid(0).ToString());
        return ids;
    }

    [Fact]
    public void MigrationSql_KeepsTheNewestRowOfEachToken_AndLeavesOtherTokensAlone()
    {
        // The migration runs on data written before the unique index existed, so drop it to recreate that state.
        Execute("DROP INDEX IF EXISTS IX_device_tokens_token");
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();
        InsertLegacyRow("00000000-0000-0000-0000-000000000001", u1, "shared", "2026-09-01 10:00:00", "2026-08-01 10:00:00");
        InsertLegacyRow("00000000-0000-0000-0000-000000000002", u2, "shared", "2026-10-02 10:00:00", "2026-09-01 10:00:00"); // newest
        InsertLegacyRow("00000000-0000-0000-0000-000000000003", u3, "shared", "2026-09-15 10:00:00", "2026-09-15 10:00:00");
        InsertLegacyRow("00000000-0000-0000-0000-000000000004", Guid.NewGuid(), "alone", "2026-09-01 10:00:00", "2026-09-01 10:00:00");

        Execute(DeviceTokenDeduplicationSql.KeepNewestRowPerToken);

        Assert.Equal(
            ["00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000004"],
            SurvivingIds());
    }

    [Fact]
    public void MigrationSql_OnATotalTie_KeepsExactlyOneRow()
    {
        Execute("DROP INDEX IF EXISTS IX_device_tokens_token");
        InsertLegacyRow("00000000-0000-0000-0000-00000000000a", Guid.NewGuid(), "tie", "2026-09-01 10:00:00", "2026-09-01 10:00:00");
        InsertLegacyRow("00000000-0000-0000-0000-00000000000b", Guid.NewGuid(), "tie", "2026-09-01 10:00:00", "2026-09-01 10:00:00");
        InsertLegacyRow("00000000-0000-0000-0000-00000000000c", Guid.NewGuid(), "tie", "2026-09-01 10:00:00", "2026-09-01 10:00:00");

        Execute(DeviceTokenDeduplicationSql.KeepNewestRowPerToken);

        Assert.Equal(["00000000-0000-0000-0000-00000000000c"], SurvivingIds());
    }

    [Fact]
    public void MigrationSql_ThenUniqueIndex_Succeeds()
    {
        Execute("DROP INDEX IF EXISTS IX_device_tokens_token");
        InsertLegacyRow("00000000-0000-0000-0000-000000000001", Guid.NewGuid(), "shared", "2026-09-01 10:00:00", "2026-09-01 10:00:00");
        InsertLegacyRow("00000000-0000-0000-0000-000000000002", Guid.NewGuid(), "shared", "2026-10-01 10:00:00", "2026-09-01 10:00:00");

        Execute(DeviceTokenDeduplicationSql.KeepNewestRowPerToken);
        Execute("CREATE UNIQUE INDEX IX_device_tokens_token ON device_tokens (token)");

        Assert.Single(SurvivingIds());
    }

    [Fact]
    public void ModelDeclaresAUniqueIndexOnToken()
    {
        var entity = _db.Model.FindEntityType(typeof(DeviceToken))!;
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["Token"]));
    }
}
