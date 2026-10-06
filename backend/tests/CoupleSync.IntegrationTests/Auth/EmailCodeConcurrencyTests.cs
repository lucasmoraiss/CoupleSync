using System.Collections.Concurrent;
using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.IntegrationTests.Auth;

/// <summary>
/// Concurrent requests for the same user and purpose, each with its own DbContext against one real (file) SQLite
/// database: the per-window cap and the code consumption must hold, not just in a single thread.
/// </summary>
[Trait("Category", "EmailCodes")]
public sealed class EmailCodeConcurrencyTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"couplesync-codes-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public EmailCodeConcurrencyTests()
    {
        _connectionString = $"Data Source={_path};Default Timeout=60";
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    private AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private sealed class FixedClock : IDateTimeProvider
    {
        public FixedClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; set; }
    }

    private sealed class CollectingSender : IEmailSender
    {
        public bool IsConfigured => true;

        public ConcurrentBag<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static VerificationCodeService Codes() =>
        new(Options.Create(new JwtOptions { Secret = "this-is-a-secure-test-secret-with-32chars" }));

    private async Task<User> SeedUserAsync(bool withExistingCode)
    {
        await using var db = NewContext();
        var user = User.Create(EmailAddress.From("ana@example.com"), "Ana", "hash", Now);
        db.Users.Add(user);
        if (withExistingCode)
        {
            db.EmailCodes.Add(EmailCode.Create(user.Id, EmailCodePurpose.PasswordReset, "seed", Now.AddMinutes(15), Now));
        }

        await db.SaveChangesAsync();
        return user;
    }

    private EmailCodeFlow NewFlow(AppDbContext db, CollectingSender sender, IDateTimeProvider clock) =>
        new(new AuthRepository(db), Codes(), sender, clock, NullLogger<EmailCodeFlow>.Instance);

    private async Task<List<Exception>> FireAsync(int requests, Func<int, Task> work)
    {
        var gate = new TaskCompletionSource();
        var errors = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, requests).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            try { await work(i); }
            catch (Exception ex) { errors.Add(ex); }
        })).ToList();
        gate.SetResult();
        await Task.WhenAll(tasks);
        return errors.ToList();
    }

    [Theory]
    [InlineData(true, 4)]  // the row exists with 1 issue: 4 more fit in the window
    [InlineData(false, 5)] // no row yet: the first inserts race, then the rest re-issue
    public async Task ConcurrentReissues_NeverExceedTheCap_AndOnlyWinnersAreEmailed(bool withExistingCode, int expectedSent)
    {
        var user = await SeedUserAsync(withExistingCode);
        var sender = new CollectingSender();
        var clock = new FixedClock(Now.AddMinutes(1));

        var errors = await FireAsync(20, async _ =>
        {
            await using var db = NewContext();
            await NewFlow(db, sender, clock).IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
        });

        Assert.Empty(errors);
        Assert.Equal(expectedSent, sender.Sent.Count);
        await using var check = NewContext();
        var stored = await check.EmailCodes.SingleAsync();
        Assert.Equal(5, stored.IssueCount);
    }

    [Fact]
    public async Task AfterTheWindow_TheBudgetStartsOver_EvenUnderConcurrency()
    {
        var user = await SeedUserAsync(withExistingCode: true);
        var sender = new CollectingSender();
        var clock = new FixedClock(Now.AddMinutes(1));
        await FireAsync(10, async _ =>
        {
            await using var db = NewContext();
            await NewFlow(db, sender, clock).IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
        });
        Assert.Equal(4, sender.Sent.Count);

        clock.UtcNow = Now.AddHours(2);
        var errors = await FireAsync(10, async _ =>
        {
            await using var db = NewContext();
            await NewFlow(db, sender, clock).IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
        });

        Assert.Empty(errors);
        Assert.Equal(4 + 5, sender.Sent.Count);
    }

    [Fact]
    public async Task ACodeReplacedAfterItWasVerified_CannotBeConsumedByTheOldVerification()
    {
        var user = await SeedUserAsync(withExistingCode: false);
        var sender = new CollectingSender();
        var clock = new FixedClock(Now.AddMinutes(1));
        await using var first = NewContext();
        var flow = NewFlow(first, sender, clock);
        await flow.IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
        var typed = System.Text.RegularExpressions.Regex.Match(sender.Sent.Single().TextContent, @"\b\d{6}\b").Value;

        var verified = await flow.VerifyAsync(user, EmailCodePurpose.PasswordReset, typed, CancellationToken.None);

        // a concurrent request re-issues the code (same row id) between verify and consume
        await using (var other = NewContext())
        {
            await NewFlow(other, sender, clock).IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
        }

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => flow.ConsumeAsync(verified, CancellationToken.None));
        Assert.Equal("INVALID_CODE", ex.Code);
        await using var check = NewContext();
        Assert.Equal(1, await check.EmailCodes.CountAsync()); // the new code survived
    }
}
