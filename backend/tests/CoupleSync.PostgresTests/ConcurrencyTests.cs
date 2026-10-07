using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.PostgresTests;

/// <summary>Real concurrent connections on real PostgreSQL (row locks, unique indexes, concurrency tokens).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresServer _server;

    public ConcurrencyTests(PostgresServer server) => _server = server;

    private static async Task<List<T>> FireAsync<T>(int requests, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource();
        var results = new ConcurrentBag<T>();
        var errors = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, requests).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            try { results.Add(await work(i)); }
            catch (Exception ex) { errors.Add(ex); }
        })).ToList();
        gate.SetResult();
        await Task.WhenAll(tasks);
        Assert.Empty(errors);
        return results.ToList();
    }

    // ---- (a) e-mail code issue cap -----------------------------------------------------------------------------

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

    private static async Task<User> SeedUserAsync(TestDatabase database, bool withExistingCode)
    {
        await using var db = MigrationTests.Context(database);
        var user = User.Create(EmailAddress.From($"ana-{Guid.NewGuid():N}@example.com"), "Ana", "hash", Now);
        db.Users.Add(user);
        if (withExistingCode)
        {
            db.EmailCodes.Add(EmailCode.Create(user.Id, EmailCodePurpose.PasswordReset, "seed", Now.AddMinutes(15), Now));
        }

        await db.SaveChangesAsync();
        return user;
    }

    private static EmailCodeFlow NewFlow(AppDbContext db, CollectingSender sender, IDateTimeProvider clock) =>
        new(new AuthRepository(db), new VerificationCodeService(Options.Create(new JwtOptions { Secret = "this-is-a-secure-test-secret-with-32chars" })),
            sender, clock, NullLogger<EmailCodeFlow>.Instance);

    private static async Task IssueConcurrentlyAsync(TestDatabase database, User user, CollectingSender sender, IDateTimeProvider clock, int requests)
    {
        await FireAsync(requests, async _ =>
        {
            await using var db = MigrationTests.Context(database);
            await NewFlow(db, sender, clock).IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
            return true;
        });
    }

    [PostgresTheory]
    [InlineData(true, 4)]  // the row exists with 1 issue: 4 more fit in the window
    [InlineData(false, 5)] // no row yet: the first inserts race on the unique index, then the rest re-issue
    public async Task EmailCodes_ConcurrentReissues_NeverExceedTheCap(bool withExistingCode, int expectedSent)
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var user = await SeedUserAsync(database, withExistingCode);
        var sender = new CollectingSender();

        await IssueConcurrentlyAsync(database, user, sender, new FixedClock(Now.AddMinutes(1)), requests: 20);

        Assert.Equal(expectedSent, sender.Sent.Count);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM email_codes"));
        Assert.Equal(5, await database.ScalarAsync<int>("SELECT issue_count FROM email_codes"));
    }

    [PostgresFact]
    public async Task EmailCodes_AfterTheWindow_TheBudgetStartsOver_EvenUnderConcurrency()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var user = await SeedUserAsync(database, withExistingCode: true);
        var sender = new CollectingSender();
        var clock = new FixedClock(Now.AddMinutes(1));
        await IssueConcurrentlyAsync(database, user, sender, clock, requests: 10);
        Assert.Equal(4, sender.Sent.Count);

        clock.UtcNow = Now.AddHours(2);
        await IssueConcurrentlyAsync(database, user, sender, clock, requests: 10);

        Assert.Equal(4 + 5, sender.Sent.Count);
        Assert.Equal(5, await database.ScalarAsync<int>("SELECT issue_count FROM email_codes"));
    }

    // ---- (b) concurrent import confirmation --------------------------------------------------------------------

    private async Task<(PostgresApiFactory Factory, TestUser User, Guid UploadId)> ReadyImportAsync(TestDatabase database, int lines)
    {
        var factory = new PostgresApiFactory(database);
        var user = await factory.RegisterAsync("Ana");
        var candidates = Enumerable.Range(0, lines)
            .Select(i => ApiFlowTests.Candidate(i, $"Linha {i}", 10m + i, $"conc-fp-{i}"))
            .ToArray();
        var uploadId = await ApiFlowTests.UploadAndMarkReadyAsync(factory, user.Client, candidates);
        return (factory, user, uploadId);
    }

    private static Task<HttpResponseMessage> ConfirmLineAsync(HttpClient client, Guid uploadId, int index) =>
        client.PostAsJsonAsync($"/api/v1/ocr/{uploadId}/confirm", new { selectedIndices = new[] { index }, keepJobOpen = true });

    [PostgresFact]
    public async Task ImportConfirmation_TheSameLineConfirmedAtOnce_CreatesOneTransaction()
    {
        await using var database = await _server.CreateDatabaseAsync();
        var (factory, user, uploadId) = await ReadyImportAsync(database, lines: 2);
        await using var _ = factory;

        var responses = await FireAsync(8, async _ =>
        {
            var response = await ConfirmLineAsync(user.Client, uploadId, 0);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        });

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, $"{r.StatusCode}: {r.Body}"));
        var created = responses.Where(r => r.StatusCode == HttpStatusCode.OK)
            .Sum(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("transactionsCreated").GetInt32());
        Assert.Equal(1, created);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions WHERE fingerprint = 'conc-fp-0'"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
    }

    [PostgresFact]
    public async Task ImportConfirmation_DifferentLinesAtOnce_NeverLoseAConfirmedMark_AndTheLoserGetsTheConflict()
    {
        await using var database = await _server.CreateDatabaseAsync();
        const int lines = 6;
        var (factory, user, uploadId) = await ReadyImportAsync(database, lines);
        await using var _ = factory;

        var responses = await FireAsync(lines, async i =>
        {
            var response = await ConfirmLineAsync(user.Client, uploadId, i);
            var code = response.StatusCode == HttpStatusCode.Conflict
                ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
                : null;
            return (Line: i, response.StatusCode, Code: code);
        });

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal("OCR_CONFIRM_CONFLICT", r.Code);
        });

        // Every line that answered 200 is stored AND marked Confirmed; no mark was overwritten by a stale write.
        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).Select(r => r.Line).OrderBy(i => i).ToArray();
        Assert.NotEmpty(winners);
        await using (var db = factory.NewContext())
        {
            var job = await db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == uploadId);
            var confirmed = Enumerable.Range(0, lines).Where(i => job.GetLineState(i) == ImportLineState.Confirmed).ToArray();
            Assert.Equal(winners, confirmed);
        }

        Assert.Equal(winners.Length, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));

        // The losers retry (what the app does after the conflict) and everything ends up confirmed exactly once.
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).Select(r => r.Line))
        {
            Assert.Equal(HttpStatusCode.OK, (await ConfirmLineAsync(user.Client, uploadId, loser)).StatusCode);
        }

        Assert.Equal(lines, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));
        Assert.Equal(lines, await database.ScalarAsync<long>("SELECT count(DISTINCT fingerprint) FROM transactions"));
        Assert.Equal("Confirmed", await database.ScalarAsync<string>($"SELECT status FROM import_jobs WHERE id = '{uploadId}'"));
    }

    // ---- (c) concurrent device-token registration ---------------------------------------------------------------

    [PostgresFact]
    public async Task DeviceTokens_ConcurrentRegistrations_NeverReturnA500_AndLeaveOneRowPerToken()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var users = new List<TestUser>();
        for (var i = 0; i < 5; i++) users.Add(await factory.RegisterAsync($"Usuario {i}"));

        // One phone handed from account to account: the same token from every user, three times each, all at once.
        var shared = await FireAsync(users.Count * 3, async i =>
            (await users[i % users.Count].Client.PostAsJsonAsync("/api/v1/devices/token", new { token = "shared-device", platform = "android" })).StatusCode);
        Assert.All(shared, status => Assert.Equal(HttpStatusCode.NoContent, status));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens WHERE token = 'shared-device'"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens"));

        // Each user refreshing its own token (a new FCM token each time) at once: exactly one row per user, none lost.
        var own = await FireAsync(users.Count * 3, async i =>
            (await users[i % users.Count].Client.PostAsJsonAsync("/api/v1/devices/token", new { token = $"own-{i}", platform = "android" })).StatusCode);
        Assert.All(own, status => Assert.Equal(HttpStatusCode.NoContent, status));
        Assert.Equal(users.Count, await database.ScalarAsync<long>("SELECT count(*) FROM device_tokens"));
        Assert.Equal(users.Count, await database.ScalarAsync<long>("SELECT count(DISTINCT user_id) FROM device_tokens"));
        Assert.Equal(users.Count, await database.ScalarAsync<long>("SELECT count(DISTINCT token) FROM device_tokens"));
    }

    // ---- e-mail code: attempts and consumption (the other ExecuteUpdate/ExecuteDelete statements) ----------------

    [PostgresFact]
    public async Task EmailCodes_WrongAttemptsAreCounted_AndACodeIsConsumedOnlyOnce()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await MigrationTests.MigrateAsync(database);
        var user = await SeedUserAsync(database, withExistingCode: false);
        var sender = new CollectingSender();
        var clock = new FixedClock(Now.AddMinutes(1));
        await using var db = MigrationTests.Context(database);
        var flow = NewFlow(db, sender, clock);
        await flow.IssueAsync(user, EmailCodePurpose.PasswordReset, CancellationToken.None);
        var typed = System.Text.RegularExpressions.Regex.Match(sender.Sent.Single().TextContent, @"\b\d{6}\b").Value;
        var wrong = typed == "000000" ? "111111" : "000000";

        await Assert.ThrowsAsync<CoupleSync.Application.Common.Exceptions.BadRequestException>(
            () => flow.VerifyAsync(user, EmailCodePurpose.PasswordReset, wrong, CancellationToken.None));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT attempts FROM email_codes"));

        var verified = await flow.VerifyAsync(user, EmailCodePurpose.PasswordReset, typed, CancellationToken.None);
        await flow.ConsumeAsync(verified, CancellationToken.None);
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM email_codes"));
        await Assert.ThrowsAsync<CoupleSync.Application.Common.Exceptions.BadRequestException>(
            () => flow.ConsumeAsync(verified, CancellationToken.None));
    }

    // ---- (d) transactions page ordering with identical timestamps -----------------------------------------------

    [PostgresFact]
    public async Task TransactionsPages_WithIdenticalTimestamps_NeverSkipOrRepeatARow()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var ana = await factory.RegisterAsync("Ana");
        var sameInstant = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        const int total = 25;
        await FireAsync(total, async i =>
            (await ana.Client.PostAsJsonAsync("/api/v1/transactions", new
            {
                amount = 10m + i, currency = "BRL", eventTimestampUtc = sameInstant, description = $"Compra {i}", category = "LAZER"
            })).StatusCode);
        Assert.Equal(total, await database.ScalarAsync<long>("SELECT count(*) FROM transactions"));

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/transactions?page={page}&pageSize=7");
            seen.AddRange(result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        }

        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());
        var expected = (await database.RowsAsync("SELECT id FROM transactions ORDER BY event_timestamp_utc DESC, id DESC"))
            .Select(r => (Guid)r[0]!).ToList();
        Assert.Equal(expected, seen);
    }
}
