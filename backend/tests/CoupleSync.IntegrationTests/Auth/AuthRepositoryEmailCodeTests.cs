using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.IntegrationTests.Auth;

/// <summary>The repository against a real (SQLite) database: transactions that roll back, and a code budget that outlives the context.</summary>
[Trait("Category", "EmailCodes")]
public sealed class AuthRepositoryEmailCodeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _connectionString = $"Data Source=couplesync-authrepo-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public AuthRepositoryEmailCodeTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAlive.Dispose();

    private AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private async Task<Guid> SeedUserWithCodeAndSessionAsync()
    {
        await using var db = NewContext();
        var user = User.Create(EmailAddress.From("ana@example.com"), "Ana", "old-hash", Now);
        db.Users.Add(user);
        db.EmailCodes.Add(EmailCode.Create(user.Id, EmailCodePurpose.PasswordReset, "code-hash", Now.AddMinutes(15), Now));
        db.RefreshTokens.Add(RefreshToken.CreateForUser(user.Id, "refresh-hash", Now.AddDays(7), Now));
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenTheActionThrows_RollsBackConsumeAndPasswordAndRevoke()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();

        await using (var db = NewContext())
        {
            var repository = new AuthRepository(db);
            var user = (await repository.FindUserByIdAsync(userId, CancellationToken.None))!;
            var code = (await repository.FindEmailCodeAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ExecuteInTransactionAsync(async () =>
            {
                await repository.ConsumeEmailCodeAsync(code.Id, CancellationToken.None);
                user.ChangePasswordHash("new-hash");
                await repository.SaveChangesAsync(CancellationToken.None);
                await repository.RevokeRefreshTokensByUserIdAsync(userId, CancellationToken.None);
                throw new InvalidOperationException("boom after everything was written");
            }, CancellationToken.None));
        }

        await using var check = NewContext();
        Assert.Equal("old-hash", (await check.Users.SingleAsync(u => u.Id == userId)).PasswordHash);
        Assert.Equal(1, await check.EmailCodes.CountAsync());
        Assert.Equal(1, await check.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenTheActionSucceeds_CommitsEverything()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();

        await using (var db = NewContext())
        {
            var repository = new AuthRepository(db);
            var user = (await repository.FindUserByIdAsync(userId, CancellationToken.None))!;
            var code = (await repository.FindEmailCodeAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;

            await repository.ExecuteInTransactionAsync(async () =>
            {
                Assert.True(await repository.ConsumeEmailCodeAsync(code.Id, CancellationToken.None));
                user.ChangePasswordHash("new-hash");
                await repository.SaveChangesAsync(CancellationToken.None);
                await repository.RevokeRefreshTokensByUserIdAsync(userId, CancellationToken.None);
            }, CancellationToken.None);
        }

        await using var check = NewContext();
        Assert.Equal("new-hash", (await check.Users.SingleAsync(u => u.Id == userId)).PasswordHash);
        Assert.Equal(0, await check.EmailCodes.CountAsync());
        Assert.Equal(0, await check.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task TheIssueCount_IsPersisted_SoANewContextSeesTheSpentBudget()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();
        var window = TimeSpan.FromHours(1);

        for (var i = 0; i < 4; i++)
        {
            await using var db = NewContext();
            var repository = new AuthRepository(db);
            var code = (await repository.FindEmailCodeForUpdateAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;
            code.Reissue($"hash-{i}", Now.AddMinutes(15), Now.AddMinutes(i + 1), window);
            await repository.StoreEmailCodeAsync(code, CancellationToken.None);
        }

        await using var fresh = NewContext();
        var stored = (await new AuthRepository(fresh).FindEmailCodeAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;
        Assert.Equal(5, stored.IssueCount);
        Assert.Equal("hash-3", stored.CodeHash);
        Assert.True(stored.HasReachedIssueLimit(Now.AddMinutes(10), 5, window));
        Assert.False(stored.HasReachedIssueLimit(Now.AddHours(2), 5, window));
    }

    [Fact]
    public async Task StoringASecondCodeForTheSameUserAndPurpose_FailsAsDbUpdateException_AndTheRepositoryRecovers()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();

        await using var db = NewContext();
        var repository = new AuthRepository(db);
        var duplicate = EmailCode.Create(userId, EmailCodePurpose.PasswordReset, "other", Now.AddMinutes(15), Now);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.StoreEmailCodeAsync(duplicate, CancellationToken.None));

        // The failed entity was detached: the retry reads the winner and updates it in place.
        var existing = (await repository.FindEmailCodeForUpdateAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;
        existing.Reissue("retry-hash", Now.AddMinutes(30), Now.AddMinutes(1), TimeSpan.FromHours(1));
        await repository.StoreEmailCodeAsync(existing, CancellationToken.None);

        await using var check = NewContext();
        var stored = await check.EmailCodes.SingleAsync();
        Assert.Equal("retry-hash", stored.CodeHash);
        Assert.Equal(2, stored.IssueCount);
    }
}
