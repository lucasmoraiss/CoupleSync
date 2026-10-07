using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Membership changes racing each other on real PostgreSQL, through the real API (each request on its own
/// connection). Whatever the interleaving, a group ends in one of two shapes: with members, exactly one of
/// whom is its owner; or with nobody, no owner and a code that no longer works. And nobody passes the limit
/// of groups per user, nor keeps an active group they do not belong to.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GroupMembershipConcurrencyTests
{
    private const int Rounds = 12;

    private static readonly HttpStatusCode[] NoServerError =
    [
        HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent, HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.Gone,
    ];

    private readonly PostgresServer _server;

    public GroupMembershipConcurrencyTests(PostgresServer server) => _server = server;

    /// <summary>Starts every action at the same instant, each on its own thread and connection.</summary>
    private static async Task<HttpStatusCode[]> AtOnceAsync(params Func<Task<HttpResponseMessage>>[] actions)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions.Select(action => Task.Run(async () =>
        {
            await gate.Task;
            using var response = await action();
            return response.StatusCode;
        })).ToList();
        gate.SetResult();
        var statuses = await Task.WhenAll(tasks);
        Assert.All(statuses, status => Assert.Contains(status, NoServerError));
        return statuses;
    }

    private static Task<HttpResponseMessage> Leave(TestUser user) => user.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

    private static Task<HttpResponseMessage> Join(TestUser user, string code) =>
        user.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = code });

    /// <summary>The invariant of every group and every user, read straight from the tables.</summary>
    private static async Task AssertConsistentAsync(TestDatabase database)
    {
        var groups = await database.RowsAsync("""
            SELECT c.id,
                   c.owner_user_id,
                   c.join_code_expires_at_utc > now() AS code_valid,
                   (SELECT count(*) FROM couple_members m WHERE m.couple_id = c.id) AS members,
                   (SELECT count(*) FROM couple_members m WHERE m.couple_id = c.id AND m.role = 'Owner') AS owners,
                   (SELECT count(*) FROM couple_members m WHERE m.couple_id = c.id AND m.role = 'Owner' AND m.user_id = c.owner_user_id) AS registered_owner_rows
            FROM couples c
            """);
        Assert.All(groups, g =>
        {
            var (id, owner, codeValid, members, owners, registered) = ((Guid)g[0]!, (Guid?)g[1], (bool)g[2]!, (long)g[3]!, (long)g[4]!, (long)g[5]!);
            if (members == 0)
            {
                Assert.True(owner is null, $"group {id} has no member but still an owner");
                Assert.False(codeValid, $"group {id} has no member but its invite code still works");
            }
            else
            {
                Assert.True(owners == 1, $"group {id} has {members} member(s) and {owners} owner(s)");
                Assert.True(registered == 1, $"group {id}: couples.owner_user_id is not its owner member");
            }
        });

        Assert.Equal(0, await database.ScalarAsync<long>("""
            SELECT count(*) FROM users u
            WHERE u.couple_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM couple_members m WHERE m.couple_id = u.couple_id AND m.user_id = u.id)
            """));
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT user_id FROM couple_members GROUP BY user_id HAVING count(*) > 5) AS over_limit"));
    }

    private static Task<long> MembersOfAsync(TestDatabase database, Guid coupleId) =>
        database.ScalarAsync<long>($"SELECT count(*) FROM couple_members WHERE couple_id = '{coupleId}'");

    [PostgresFact]
    public async Task TheLastTwoMembersLeavingAtOnce_LeaveAnEmptyGroupThatNobodyCanJoin()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);

        for (var round = 0; round < Rounds; round++)
        {
            var owner = await factory.RegisterAsync("Dona");
            var member = await factory.RegisterAsync("Membro", joinCode: owner.JoinCode);

            var statuses = await AtOnceAsync(() => Leave(owner), () => Leave(member));

            Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
            Assert.Equal(0, await MembersOfAsync(database, owner.CoupleId!.Value));
            await AssertConsistentAsync(database);

            var stranger = await factory.RegisterAsync("Estranho", createGroup: false);
            using var join = await Join(stranger, owner.JoinCode!);
            Assert.Equal(HttpStatusCode.Gone, join.StatusCode);
        }
    }

    [PostgresFact]
    public async Task AJoinOverlappingTheLastLeave_EitherFindsTheCodeDead_OrTheJoinerBecomesTheOwner()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var outcomes = new ConcurrentBag<HttpStatusCode>();

        for (var round = 0; round < Rounds; round++)
        {
            var last = await factory.RegisterAsync("Última");
            var joiner = await factory.RegisterAsync("Chegando", createGroup: false);
            var coupleId = last.CoupleId!.Value;

            var statuses = await AtOnceAsync(() => Leave(last), () => Join(joiner, last.JoinCode!));

            Assert.Equal(HttpStatusCode.OK, statuses[0]);
            outcomes.Add(statuses[1]);
            await AssertConsistentAsync(database);
            if (statuses[1] == HttpStatusCode.OK)
            {
                Assert.Equal(1, await MembersOfAsync(database, coupleId));
                Assert.Equal(joiner.UserId, await database.ScalarAsync<Guid>($"SELECT owner_user_id FROM couples WHERE id = '{coupleId}'"));
            }
            else
            {
                Assert.Equal(HttpStatusCode.Gone, statuses[1]);
                Assert.Equal(0, await MembersOfAsync(database, coupleId));
            }
        }

        Assert.All(outcomes, status => Assert.Contains(status, new[] { HttpStatusCode.OK, HttpStatusCode.Gone }));
    }

    [PostgresFact]
    public async Task EveryoneLeavingWhileOthersJoinAndTheOwnerRenewsTheCodeAndRemovesAMember_NeverBreaksAGroup()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);

        for (var round = 0; round < Rounds; round++)
        {
            var owner = await factory.RegisterAsync("Dona");
            var second = await factory.RegisterAsync("Segundo", joinCode: owner.JoinCode);
            var third = await factory.RegisterAsync("Terceira", joinCode: owner.JoinCode);
            var newcomers = new[]
            {
                await factory.RegisterAsync("Nova 1", createGroup: false),
                await factory.RegisterAsync("Novo 2", createGroup: false),
            };

            await AtOnceAsync(
                () => Leave(owner),
                () => Leave(second),
                () => Leave(third),
                () => owner.Client.PostAsJsonAsync("/api/v1/couples/join-code", new { }),
                () => owner.Client.DeleteAsync($"/api/v1/couples/members/{second.UserId}"),
                () => Join(newcomers[0], owner.JoinCode!),
                () => Join(newcomers[1], owner.JoinCode!));

            await AssertConsistentAsync(database);
            // The three original members are out, whichever way each of them went.
            Assert.Equal(0, await database.ScalarAsync<long>(
                $"SELECT count(*) FROM couple_members WHERE couple_id = '{owner.CoupleId}' AND user_id IN ('{owner.UserId}', '{second.UserId}', '{third.UserId}')"));
        }
    }

    [PostgresFact]
    public async Task ParallelCreatesAndJoins_NeverTakeAUserPastFiveGroups()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);
        var hosts = new[] { await factory.RegisterAsync("Anfitriã 1"), await factory.RegisterAsync("Anfitrião 2") };
        var ana = await factory.RegisterAsync("Ana");
        for (var i = 0; i < 2; i++)
        {
            (await ana.Client.PostAsJsonAsync("/api/v1/couples", new { })).EnsureSuccessStatusCode();
        }

        // Ana has 3 groups; ten more arrive at once (8 creations, 2 joins): only two fit.
        var actions = Enumerable.Range(0, 8)
            .Select(_ => (Func<Task<HttpResponseMessage>>)(() => ana.Client.PostAsJsonAsync("/api/v1/couples", new { })))
            .Concat(hosts.Select(host => (Func<Task<HttpResponseMessage>>)(() => Join(ana, host.JoinCode!))))
            .ToArray();
        var statuses = await AtOnceAsync(actions);

        Assert.Equal(2, statuses.Count(s => s is HttpStatusCode.Created or HttpStatusCode.OK));
        Assert.Equal(8, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(5, await database.ScalarAsync<long>($"SELECT count(*) FROM couple_members WHERE user_id = '{ana.UserId}'"));
        await AssertConsistentAsync(database);
    }

    [PostgresFact]
    public async Task SwitchingIntoAGroupWhileBeingRemovedFromIt_NeverLeavesAnActiveGroupWithoutMembership()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);

        for (var round = 0; round < Rounds; round++)
        {
            var owner = await factory.RegisterAsync("Dona");
            var member = await factory.RegisterAsync("Membro", joinCode: owner.JoinCode);
            var created = await member.Client.PostAsJsonAsync("/api/v1/couples", new { }); // the member's own group becomes active
            var own = await created.Content.ReadFromJsonAsync<JsonElement>();
            member.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", own.GetProperty("accessToken").GetString());

            var statuses = await AtOnceAsync(
                () => member.Client.PostAsJsonAsync("/api/v1/couples/switch", new { coupleId = owner.CoupleId }),
                () => owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}"));

            Assert.Equal(HttpStatusCode.NoContent, statuses[1]);
            await AssertConsistentAsync(database);
            var active = await database.ScalarAsync<Guid?>($"SELECT couple_id FROM users WHERE id = '{member.UserId}'");
            if (statuses[0] == HttpStatusCode.OK)
            {
                Assert.Null(active); // switched in first, then removed from what had become the active group
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, statuses[0]);
                Assert.Equal(own.GetProperty("coupleId").GetGuid(), active);
            }

            // Whatever the order, the removed member's token for that group reads nothing of it.
            using var data = await member.Client.GetAsync("/api/v1/transactions");
            Assert.True(data.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden);
            Assert.Equal(0, await database.ScalarAsync<long>(
                $"SELECT count(*) FROM couple_members WHERE couple_id = '{owner.CoupleId}' AND user_id = '{member.UserId}'"));
        }
    }
}
