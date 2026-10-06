using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.IntegrationTests.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.Couples;

/// <summary>
/// One user in several groups, over HTTP: the token carries the active group, every data route stays inside
/// it, and belonging to a group is always decided by the membership table (never by the token, never by a
/// group id sent in a request).
/// </summary>
[Trait("Category", "MultiGroup")]
public sealed class MultiGroupIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Actor
    {
        public required HttpClient Client { get; init; }
        public required Guid UserId { get; init; }
        public required string Email { get; init; }
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;

        public void Authenticate(string accessToken)
        {
            AccessToken = accessToken;
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        /// <summary>Takes the token pair of a response (create, join, switch, leave) the way the app does.</summary>
        public void Take(JsonElement body)
        {
            Authenticate(body.GetProperty("accessToken").GetString()!);
            if (body.TryGetProperty("refreshToken", out var refresh) && refresh.ValueKind == JsonValueKind.String)
            {
                RefreshToken = refresh.GetString()!;
            }
        }
    }

    private sealed record Group(Guid Id, string Code);

    private static async Task<Actor> RegisterAsync(TransactionWebApplicationFactory factory, string name)
    {
        var client = factory.CreateClient();
        var email = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = name, Password = "SecurePass123!" });
        register.EnsureSuccessStatusCode();
        var body = await register.Content.ReadFromJsonAsync<JsonElement>(Json);
        var actor = new Actor { Client = client, UserId = body.GetProperty("user").GetProperty("id").GetGuid(), Email = email };
        actor.Take(body);
        return actor;
    }

    private static async Task<Group> CreateGroupAsync(Actor actor)
    {
        var created = await actor.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        actor.Take(body);
        return new Group(body.GetProperty("coupleId").GetGuid(), body.GetProperty("joinCode").GetString()!);
    }

    private static async Task JoinAsync(Actor actor, Group group)
    {
        var join = await actor.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = group.Code });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        actor.Take(await join.Content.ReadFromJsonAsync<JsonElement>(Json));
    }

    private static async Task SwitchAsync(Actor actor, Guid coupleId)
    {
        var response = await actor.Client.PostAsJsonAsync("/api/v1/couples/switch", new { CoupleId = coupleId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(coupleId, body.GetProperty("coupleId").GetGuid());
        actor.Take(body);
    }

    private static async Task<Guid> AddTransactionAsync(Actor actor, string description, decimal amount = 10m, object? extra = null)
    {
        var response = await actor.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            Amount = amount, Currency = "BRL", Description = description, Category = "Alimentação", EventTimestampUtc = DateTime.UtcNow,
            CoupleId = extra,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static async Task<string[]> DescriptionsAsync(Actor actor, string query = "")
    {
        var list = await actor.Client.GetFromJsonAsync<JsonElement>($"/api/v1/transactions{query}", Json);
        return list.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("description").GetString()!).Order().ToArray();
    }

    private static async Task<JsonElement> MyGroupsAsync(Actor actor) =>
        await actor.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples", Json);

    private static Guid[] GroupIdsOf(JsonElement myGroups) =>
        myGroups.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("coupleId").GetGuid()).ToArray();

    private static async Task<string> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString()!;

    private static Guid? CoupleClaimOf(string accessToken)
    {
        var value = new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.FirstOrDefault(c => c.Type == "couple_id")?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static async Task<HttpResponseMessage> RefreshAsync(TransactionWebApplicationFactory factory, string refreshToken)
    {
        using var anonymous = factory.CreateClient();
        return await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = refreshToken });
    }

    [Fact]
    public async Task SecondGroup_BecomesActive_AndEachGroupsDataStaysInItsGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var first = await CreateGroupAsync(ana);
        await AddTransactionAsync(ana, "no primeiro");
        var tokenOfFirst = ana.AccessToken;

        var second = await CreateGroupAsync(ana);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, CoupleClaimOf(ana.AccessToken));
        Assert.Empty(await DescriptionsAsync(ana));
        await AddTransactionAsync(ana, "no segundo");
        Assert.Equal(["no segundo"], await DescriptionsAsync(ana));

        await SwitchAsync(ana, first.Id);

        Assert.Equal(first.Id, CoupleClaimOf(ana.AccessToken));
        Assert.Equal(["no primeiro"], await DescriptionsAsync(ana));

        // A token issued for the first group before the switch still reads the first group only (she is a member of it).
        ana.Authenticate(tokenOfFirst);
        Assert.Equal(["no primeiro"], await DescriptionsAsync(ana));
    }

    [Fact]
    public async Task JoiningAnotherGroup_KeepsTheFirst_AndMakesTheJoinedOneActive()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var own = await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno");
        var brunos = await CreateGroupAsync(bruno);
        await AddTransactionAsync(bruno, "do Bruno");

        await JoinAsync(ana, brunos);

        Assert.Equal(brunos.Id, CoupleClaimOf(ana.AccessToken));
        Assert.Equal(["do Bruno"], await DescriptionsAsync(ana));
        var groups = await MyGroupsAsync(ana);
        Assert.Equal(new[] { own.Id, brunos.Id }.Order(), GroupIdsOf(groups).Order());
        Assert.Equal(brunos.Id, groups.GetProperty("activeCoupleId").GetGuid());

        // Bruno is not in Ana's own group and sees nothing of it.
        Assert.Equal([brunos.Id], GroupIdsOf(await MyGroupsAsync(bruno)));
    }

    [Fact]
    public async Task MyGroups_ListsOnlyTheCallersGroups_WithRoleActiveFlagAndLabel()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana Souza");
        var alone = await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno Lima");
        var brunos = await CreateGroupAsync(bruno);
        var carla = await RegisterAsync(factory, "Carla");
        var carlas = await CreateGroupAsync(carla);
        await JoinAsync(ana, brunos);

        var body = await MyGroupsAsync(ana);

        Assert.Equal(5, body.GetProperty("maxGroups").GetInt32());
        var groups = body.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("coupleId").GetGuid());
        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(carlas.Id, groups.Keys);
        Assert.True(groups[alone.Id].GetProperty("isOwner").GetBoolean());
        Assert.False(groups[alone.Id].GetProperty("isActive").GetBoolean());
        Assert.Equal("Grupo só seu", groups[alone.Id].GetProperty("name").GetString());
        Assert.False(groups[brunos.Id].GetProperty("isOwner").GetBoolean());
        Assert.True(groups[brunos.Id].GetProperty("isActive").GetBoolean());
        Assert.Equal("Grupo com Bruno", groups[brunos.Id].GetProperty("name").GetString());
        Assert.Equal(2, groups[brunos.Id].GetProperty("members").GetArrayLength());

        // No e-mail addresses, codes or anything else of the groups in this list.
        Assert.DoesNotContain("@", body.GetRawText());
        Assert.DoesNotContain(brunos.Code, body.GetRawText());

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/couples")).StatusCode);
    }

    [Fact]
    public async Task Switch_ToAGroupTheUserIsNotIn_IsRefused_AndChangesNothing()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var own = await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno");
        var brunos = await CreateGroupAsync(bruno);
        await AddTransactionAsync(bruno, "do Bruno");

        foreach (var target in new[] { brunos.Id, Guid.NewGuid(), Guid.Empty })
        {
            var response = await ana.Client.PostAsJsonAsync("/api/v1/couples/switch", new { CoupleId = target });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("COUPLE_NOT_FOUND", await CodeOf(response));
        }

        Assert.Equal(own.Id, (await MyGroupsAsync(ana)).GetProperty("activeCoupleId").GetGuid());
        var refreshed = await RefreshAsync(factory, ana.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(own.Id, CoupleClaimOf((await refreshed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!));
        Assert.Empty(await DescriptionsAsync(ana));
    }

    [Fact]
    public async Task Switch_ReplacesTheRefreshToken_AndRenewalsAndNewLoginsFollowTheActiveGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var first = await CreateGroupAsync(ana);
        var second = await CreateGroupAsync(ana);
        var refreshBeforeSwitch = ana.RefreshToken;

        await SwitchAsync(ana, first.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(factory, refreshBeforeSwitch)).StatusCode);
        var refreshed = await RefreshAsync(factory, ana.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(first.Id, CoupleClaimOf((await refreshed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!));

        using var anotherDevice = factory.CreateClient();
        var login = await anotherDevice.PostAsJsonAsync("/api/v1/auth/login", new { ana.Email, Password = "SecurePass123!" });
        Assert.Equal(first.Id, CoupleClaimOf((await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!));
        Assert.NotEqual(second.Id, first.Id);
    }

    [Fact]
    public async Task RemovedFromTheActiveGroup_DataIsRefused_ButTheUserStillListsAndSwitchesToTheirOtherGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var owner = await RegisterAsync(factory, "Dono");
        var shared = await CreateGroupAsync(owner);
        var member = await RegisterAsync(factory, "Membro");
        var own = await CreateGroupAsync(member);
        await AddTransactionAsync(member, "só meu");
        await JoinAsync(member, shared); // the shared group is now the member's active one
        var refreshBeforeRemoval = member.RefreshToken;

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}")).StatusCode);

        // Same, still valid, access token (its claim says "shared").
        var data = await member.Client.GetAsync("/api/v1/transactions");
        Assert.Equal(HttpStatusCode.Forbidden, data.StatusCode);
        Assert.Equal("COUPLE_REQUIRED", await CodeOf(data));
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync("/api/v1/couples/me")).StatusCode);

        // Someone else's act never moves the user into another group: no group is active until they choose.
        var groups = await MyGroupsAsync(member);
        Assert.Equal([own.Id], GroupIdsOf(groups));
        Assert.Equal(JsonValueKind.Null, groups.GetProperty("activeCoupleId").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(factory, refreshBeforeRemoval)).StatusCode);

        var back = await member.Client.PostAsJsonAsync("/api/v1/couples/switch", new { CoupleId = shared.Id });
        Assert.Equal(HttpStatusCode.NotFound, back.StatusCode);

        await SwitchAsync(member, own.Id);
        Assert.Equal(["só meu"], await DescriptionsAsync(member));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(factory, member.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task RemovedFromAGroupThatIsNotTheActiveOne_KeepsWorkingInTheActiveGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var owner = await RegisterAsync(factory, "Dono");
        var shared = await CreateGroupAsync(owner);
        var member = await RegisterAsync(factory, "Membro");
        await JoinAsync(member, shared);
        var tokenOfShared = member.AccessToken;
        var own = await CreateGroupAsync(member); // active: own
        await AddTransactionAsync(member, "só meu");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}")).StatusCode);

        Assert.Equal(["só meu"], await DescriptionsAsync(member));
        var groups = await MyGroupsAsync(member);
        Assert.Equal([own.Id], GroupIdsOf(groups));
        Assert.Equal(own.Id, groups.GetProperty("activeCoupleId").GetGuid());
        var refreshed = await RefreshAsync(factory, member.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(own.Id, CoupleClaimOf((await refreshed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!));

        // The older token that still names the group they were removed from reads nothing.
        member.Authenticate(tokenOfShared);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync("/api/v1/transactions")).StatusCode);
    }

    [Fact]
    public async Task LeavingTheActiveGroup_ActivatesTheOldestRemainingGroup_AndThenNone()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var first = await CreateGroupAsync(ana);
        await AddTransactionAsync(ana, "no primeiro");
        var second = await CreateGroupAsync(ana);
        var third = await CreateGroupAsync(ana); // active: third
        var tokenOfThird = ana.AccessToken;

        var leave = await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

        Assert.Equal(HttpStatusCode.OK, leave.StatusCode);
        var body = await leave.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(first.Id, body.GetProperty("activeCoupleId").GetGuid());
        ana.Take(body);
        Assert.Equal(first.Id, CoupleClaimOf(ana.AccessToken));
        Assert.Equal(["no primeiro"], await DescriptionsAsync(ana));
        Assert.Equal(new[] { first.Id, second.Id }.Order(), GroupIdsOf(await MyGroupsAsync(ana)).Order());

        // The token of the group she left is refused there.
        var stale = factory.CreateClient();
        stale.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenOfThird);
        Assert.Equal(HttpStatusCode.Forbidden, (await stale.GetAsync("/api/v1/transactions")).StatusCode);

        ana.Take(await (await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).Content.ReadFromJsonAsync<JsonElement>(Json));
        Assert.Equal(second.Id, CoupleClaimOf(ana.AccessToken));
        var last = await (await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("activeCoupleId").ValueKind);
        ana.Take(last);
        Assert.Null(CoupleClaimOf(ana.AccessToken));
        Assert.Empty(GroupIdsOf(await MyGroupsAsync(ana)));
        Assert.Equal(HttpStatusCode.NotFound, (await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);
    }

    [Fact]
    public async Task LeavingAGroupByItsId_KeepsTheActiveGroup_AndOnlyWorksForOnesOwnGroups()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var first = await CreateGroupAsync(ana);
        var second = await CreateGroupAsync(ana); // active: second
        var bruno = await RegisterAsync(factory, "Bruno");
        var brunos = await CreateGroupAsync(bruno);

        foreach (var foreign in new[] { brunos.Id, Guid.NewGuid() })
        {
            var refused = await ana.Client.PostAsJsonAsync($"/api/v1/couples/{foreign}/leave", new { });
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal("COUPLE_NOT_FOUND", await CodeOf(refused));
        }

        Assert.Equal([brunos.Id], GroupIdsOf(await MyGroupsAsync(bruno)));
        var me = await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me", Json);
        Assert.Equal(bruno.UserId, me.GetProperty("ownerUserId").GetGuid());

        var leave = await ana.Client.PostAsJsonAsync($"/api/v1/couples/{first.Id}/leave", new { });
        Assert.Equal(HttpStatusCode.OK, leave.StatusCode);
        var body = await leave.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(second.Id, body.GetProperty("activeCoupleId").GetGuid());
        ana.Take(body);
        Assert.Equal(second.Id, CoupleClaimOf(ana.AccessToken));
        Assert.Equal([second.Id], GroupIdsOf(await MyGroupsAsync(ana)));
    }

    [Fact]
    public async Task AUserCanBeInFiveGroupsAtMost()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var bruno = await RegisterAsync(factory, "Bruno");
        var brunos = await CreateGroupAsync(bruno);
        var ana = await RegisterAsync(factory, "Ana");
        var groups = new List<Group>();
        for (var i = 0; i < 5; i++)
        {
            groups.Add(await CreateGroupAsync(ana));
        }

        var sixth = await ana.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);
        var error = await sixth.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("GROUP_LIMIT_REACHED", error.GetProperty("code").GetString());
        Assert.Contains("5 grupos", error.GetProperty("message").GetString());

        var join = await ana.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = brunos.Code });
        Assert.Equal(HttpStatusCode.Conflict, join.StatusCode);
        Assert.Equal("GROUP_LIMIT_REACHED", await CodeOf(join));
        Assert.Equal(5, GroupIdsOf(await MyGroupsAsync(ana)).Length);
        Assert.Equal(groups[4].Id, CoupleClaimOf(ana.AccessToken));

        ana.Take(await (await ana.Client.PostAsJsonAsync($"/api/v1/couples/{groups[0].Id}/leave", new { })).Content.ReadFromJsonAsync<JsonElement>(Json));
        await JoinAsync(ana, brunos);
        Assert.Equal(5, GroupIdsOf(await MyGroupsAsync(ana)).Length);
    }

    [Fact]
    public async Task JoiningAGroupOneIsAlreadyIn_IsAConflict()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var own = await CreateGroupAsync(ana);

        var again = await ana.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = own.Code });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("USER_ALREADY_IN_COUPLE", await CodeOf(again));
        Assert.Equal([own.Id], GroupIdsOf(await MyGroupsAsync(ana)));
    }

    [Fact]
    public async Task OwnerOnlyActions_AreDecidedPerGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var anas = await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno");
        var brunos = await CreateGroupAsync(bruno);
        await JoinAsync(ana, brunos); // Ana: owner of her own group, plain member of Bruno's (now active)

        var renew = await ana.Client.PostAsJsonAsync("/api/v1/couples/join-code", new { });
        Assert.Equal(HttpStatusCode.Forbidden, renew.StatusCode);
        Assert.Equal("NOT_COUPLE_OWNER", await CodeOf(renew));
        var remove = await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);

        await SwitchAsync(ana, anas.Id);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync("/api/v1/couples/join-code", new { })).StatusCode);
        // Bruno is not in Ana's group: being its owner gives her no hold over him.
        Assert.Equal(HttpStatusCode.NotFound, (await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")).StatusCode);
        Assert.Equal(2, (await bruno.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me", Json)).GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task DataRoutes_IgnoreAGroupIdSentInTheRequest()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno");
        var brunos = await CreateGroupAsync(bruno);
        await AddTransactionAsync(bruno, "do Bruno");

        await AddTransactionAsync(ana, "da Ana", extra: brunos.Id);

        Assert.Equal(["da Ana"], await DescriptionsAsync(ana, $"?coupleId={brunos.Id}"));
        Assert.Equal(["do Bruno"], await DescriptionsAsync(bruno));
    }

    [Fact]
    public async Task AlertPreferences_AreKeptPerGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var first = await CreateGroupAsync(ana);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.PutAsJsonAsync("/api/v1/notifications/settings", new { LargeTransactionEnabled = false })).StatusCode);

        await CreateGroupAsync(ana);

        var inSecond = await ana.Client.GetAsync("/api/v1/notifications/settings");
        Assert.Equal(HttpStatusCode.OK, inSecond.StatusCode);
        Assert.True((await inSecond.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("largeTransactionEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.PutAsJsonAsync("/api/v1/notifications/settings", new { LowBalanceEnabled = false })).StatusCode);

        await SwitchAsync(ana, first.Id);
        var inFirst = await ana.Client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/settings", Json);
        Assert.False(inFirst.GetProperty("largeTransactionEnabled").GetBoolean());
        Assert.True(inFirst.GetProperty("lowBalanceEnabled").GetBoolean());
    }

    [Fact]
    public async Task AnAlertOfAGroup_IsQueuedOnlyForThatGroupsMembers_WhicheverGroupTheyHaveActive()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var withBruno = await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno");
        await JoinAsync(bruno, withBruno);
        var carla = await RegisterAsync(factory, "Carla");
        var withCarla = await CreateGroupAsync(carla);
        await JoinAsync(ana, withCarla);
        await SwitchAsync(ana, withBruno.Id); // Ana works in the group with Bruno; the alert comes from the other one

        await AddTransactionAsync(carla, "compra grande", amount: 900m);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerts = await db.NotificationEvents.IgnoreQueryFilters().Where(e => e.AlertType == "LargeTransaction").ToListAsync();
        Assert.All(alerts, alert => Assert.Equal(withCarla.Id, alert.CoupleId));
        Assert.Equal(new[] { ana.UserId, carla.UserId }.Order(), alerts.Select(a => a.UserId).Order());
        Assert.DoesNotContain(alerts, alert => alert.UserId == bruno.UserId);
    }

    [Fact]
    public async Task LeavingOneOfSeveralGroups_KeepsTheDevice_LeavingTheLastForgetsIt()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var first = await CreateGroupAsync(ana);
        var second = await CreateGroupAsync(ana);
        Assert.Equal(HttpStatusCode.NoContent,
            (await ana.Client.PostAsJsonAsync("/api/v1/devices/token", new { Token = "fcm-ana", Platform = "android" })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NotificationEvents.Add(NotificationEvent.Create(second.Id, ana.UserId, "LargeTransaction", "t", "b", DateTime.UtcNow));
            db.NotificationEvents.Add(NotificationEvent.Create(first.Id, ana.UserId, "LargeTransaction", "t", "b", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        ana.Take(await (await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).Content.ReadFromJsonAsync<JsonElement>(Json));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.DeviceTokens.IgnoreQueryFilters().SingleAsync(d => d.UserId == ana.UserId);
            Assert.Equal("fcm-ana", device.Token);
            Assert.Equal(first.Id, device.CoupleId);
            var events = await db.NotificationEvents.IgnoreQueryFilters().Where(e => e.UserId == ana.UserId).ToListAsync();
            Assert.NotEqual("Pending", events.Single(e => e.CoupleId == second.Id).Status);
        }

        ana.Take(await (await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).Content.ReadFromJsonAsync<JsonElement>(Json));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.DeviceTokens.IgnoreQueryFilters().Where(d => d.UserId == ana.UserId).ToListAsync());
        }
    }

    [Fact]
    public async Task MembershipRows_MirrorTheGroupsOwnerAndMembers()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var ana = await RegisterAsync(factory, "Ana");
        var group = await CreateGroupAsync(ana);
        var bruno = await RegisterAsync(factory, "Bruno");
        await JoinAsync(bruno, group);

        ana.Take(await (await ana.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).Content.ReadFromJsonAsync<JsonElement>(Json));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.CoupleMembers.SingleAsync(m => m.CoupleId == group.Id);
        Assert.Equal(bruno.UserId, row.UserId);
        Assert.Equal(CoupleRole.Owner, row.Role);
        Assert.Equal(bruno.UserId, (await db.Couples.SingleAsync(c => c.Id == group.Id)).OwnerUserId);
        Assert.Null((await db.Users.SingleAsync(u => u.Id == ana.UserId)).ActiveCoupleId);
    }
}
