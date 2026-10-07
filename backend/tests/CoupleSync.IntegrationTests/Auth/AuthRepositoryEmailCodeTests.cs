using CoupleSync.Application.Common.Interfaces;
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
                await repository.ConsumeEmailCodeAsync(code.Id, code.CodeHash, CancellationToken.None);
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
                Assert.True(await repository.ConsumeEmailCodeAsync(code.Id, code.CodeHash, CancellationToken.None));
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
    public async Task TheIssueCount_IsPersisted_AndTheCapIsEnforcedByTheUpdateItself()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();
        var window = TimeSpan.FromHours(1);

        for (var i = 0; i < 4; i++)
        {
            await using var db = NewContext();
            var result = await new AuthRepository(db).TryReissueEmailCodeAsync(
                userId, EmailCodePurpose.PasswordReset, $"hash-{i}", Now.AddMinutes(15), Now.AddMinutes(i + 1), 5, window, CancellationToken.None);
            Assert.Equal(EmailCodeReissueResult.Reissued, result);
        }

        await using (var db = NewContext())
        {
            var limited = await new AuthRepository(db).TryReissueEmailCodeAsync(
                userId, EmailCodePurpose.PasswordReset, "hash-too-many", Now.AddMinutes(15), Now.AddMinutes(10), 5, window, CancellationToken.None);
            Assert.Equal(EmailCodeReissueResult.LimitReached, limited);
        }

        await using var fresh = NewContext();
        var stored = (await new AuthRepository(fresh).FindEmailCodeAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;
        Assert.Equal(5, stored.IssueCount);
        Assert.Equal("hash-3", stored.CodeHash); // the refused request changed nothing

        await using (var db = NewContext())
        {
            var afterWindow = await new AuthRepository(db).TryReissueEmailCodeAsync(
                userId, EmailCodePurpose.PasswordReset, "hash-new-window", Now.AddHours(3), Now.AddHours(2), 5, window, CancellationToken.None);
            Assert.Equal(EmailCodeReissueResult.Reissued, afterWindow);
        }

        await using var last = NewContext();
        var reset = await last.EmailCodes.AsNoTracking().SingleAsync();
        Assert.Equal(1, reset.IssueCount);
        Assert.Equal(Now.AddHours(2), reset.IssueWindowStartedAtUtc);
        Assert.Equal(0, reset.Attempts);
    }

    [Fact]
    public async Task ReissueWithoutACode_ReportsNoCode_AndASecondInsertFailsAsUniqueViolation_LeavingTheEntityDetached()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();

        await using var db = NewContext();
        var repository = new AuthRepository(db);
        Assert.Equal(EmailCodeReissueResult.NoCode, await repository.TryReissueEmailCodeAsync(
            userId, EmailCodePurpose.EmailVerification, "h", Now.AddMinutes(15), Now, 5, TimeSpan.FromHours(1), CancellationToken.None));

        var duplicate = EmailCode.Create(userId, EmailCodePurpose.PasswordReset, "other", Now.AddMinutes(15), Now);
        await Assert.ThrowsAsync<CoupleSync.Application.Common.Exceptions.UniqueViolationException>(() => repository.AddEmailCodeAsync(duplicate, CancellationToken.None));

        // The failed entity was detached: it is no longer tracked and the same context can save again.
        Assert.Equal(EntityState.Detached, db.Entry(duplicate).State);
        await repository.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(EmailCodeReissueResult.Reissued, await repository.TryReissueEmailCodeAsync(
            userId, EmailCodePurpose.PasswordReset, "retry-hash", Now.AddMinutes(30), Now.AddMinutes(1), 5, TimeSpan.FromHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task Consume_OnlyDeletesTheCodeThatWasVerified()
    {
        var userId = await SeedUserWithCodeAndSessionAsync();
        await using var db = NewContext();
        var repository = new AuthRepository(db);
        var code = (await repository.FindEmailCodeAsync(userId, EmailCodePurpose.PasswordReset, CancellationToken.None))!;

        Assert.False(await repository.ConsumeEmailCodeAsync(code.Id, "some-other-hash", CancellationToken.None));
        Assert.True(await repository.ConsumeEmailCodeAsync(code.Id, code.CodeHash, CancellationToken.None));
        Assert.False(await repository.ConsumeEmailCodeAsync(code.Id, code.CodeHash, CancellationToken.None));
    }
}
