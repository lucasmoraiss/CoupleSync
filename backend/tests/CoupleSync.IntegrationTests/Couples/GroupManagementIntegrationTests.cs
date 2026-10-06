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
/// Leaving a group, removing a member, renewing the invite code and its expiry, over HTTP. The central
/// guarantee: an access token issued before a removal (still valid for its 15 minutes) stops reading and
/// writing the group's data the moment the removal commits.
/// </summary>
[Trait("Category", "GroupManagement")]
public sealed class GroupManagementIntegrationTests
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
    }

    private static async Task<Actor> RegisterAsync(TransactionWebApplicationFactory factory, string label)
    {
        var client = factory.CreateClient();
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = label, Password = "SecurePass123!" });
        register.EnsureSuccessStatusCode();
        var body = await register.Content.ReadFromJsonAsync<JsonElement>(Json);
        var actor = new Actor { Client = client, UserId = body.GetProperty("user").GetProperty("id").GetGuid(), Email = email };
        actor.RefreshToken = body.GetProperty("refreshToken").GetString()!;
        actor.Authenticate(body.GetProperty("accessToken").GetString()!);
        return actor;
    }

    /// <summary>The owner creates the group; the others join with its code. Every actor ends with a token that carries the group.</summary>
    private static async Task<(Actor Owner, Actor Member, Actor Third, string Code)> NewGroupOfThreeAsync(TransactionWebApplicationFactory factory)
    {
        var owner = await RegisterAsync(factory, "owner");
        var created = await owner.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        owner.Authenticate(createdBody.GetProperty("accessToken").GetString()!);
        var code = createdBody.GetProperty("joinCode").GetString()!;

        var member = await RegisterAsync(factory, "member");
        await JoinAsync(member, code);
        var third = await RegisterAsync(factory, "third");
        await JoinAsync(third, code);
        return (owner, member, third, code);
    }

    private static async Task JoinAsync(Actor actor, string code)
    {
        var join = await actor.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = code });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        actor.Authenticate((await join.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    private static string? CoupleClaimOf(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.FirstOrDefault(c => c.Type == "couple_id")?.Value;

    // The brief's central test: remove a member, then call data endpoints with their still-valid token.

    [Fact]
    public async Task RemovedMember_OldAccessToken_CannotReadOrWriteTheGroupsData()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, _, _) = await NewGroupOfThreeAsync(factory);
        var oldToken = member.AccessToken;
        Assert.Equal(HttpStatusCode.OK, (await member.Client.GetAsync("/api/v1/transactions")).StatusCode);

        var removal = await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

        // The token is untouched and unexpired: only the server-side membership check can refuse it.
        member.Authenticate(oldToken);
        var read = await member.Client.GetAsync("/api/v1/transactions");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("COUPLE_REQUIRED", (await ErrorOf(read)).GetProperty("code").GetString());

        var write = await member.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            Amount = 10m, Currency = "BRL", Description = "x", Category = "Alimentação", EventTimestampUtc = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        foreach (var path in new[] { "/api/v1/goals", "/api/v1/dashboard", "/api/v1/incomes/current", "/api/v1/budgets/current" })
        {
            var response = await member.Client.GetAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden, $"{path} answered {(int)response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync("/api/v1/couples/me")).StatusCode);
        // The remaining members are unaffected.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync("/api/v1/transactions")).StatusCode);
    }

    [Fact]
    public async Task MemberWhoLeft_OldAccessToken_CannotReadTheGroupsData()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, _, _) = await NewGroupOfThreeAsync(factory);
        var oldToken = member.AccessToken;

        Assert.Equal(HttpStatusCode.OK, (await member.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);

        member.Authenticate(oldToken);
        var read = await member.Client.GetAsync("/api/v1/transactions");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync("/api/v1/transactions")).StatusCode);
    }

    [Fact]
    public async Task Leave_ReturnsTokensWithoutGroup_AndTheOldRefreshTokenStopsWorking()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (_, member, _, _) = await NewGroupOfThreeAsync(factory);
        var oldRefresh = member.RefreshToken;

        var leave = await member.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

        Assert.Equal(HttpStatusCode.OK, leave.StatusCode);
        var body = await leave.Content.ReadFromJsonAsync<JsonElement>(Json);
        var newAccess = body.GetProperty("accessToken").GetString()!;
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        Assert.True(string.IsNullOrEmpty(CoupleClaimOf(newAccess)));

        var stale = await member.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = oldRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var renewed = await member.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = newRefresh });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var renewedAccess = (await renewed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!;
        Assert.True(string.IsNullOrEmpty(CoupleClaimOf(renewedAccess)));

        member.Authenticate(renewedAccess);
        var read = await member.Client.GetAsync("/api/v1/transactions");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public async Task RemovedMember_RefreshTokenIsRevoked_AndANewLoginHasNoGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, _, _) = await NewGroupOfThreeAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}")).StatusCode);

        var refresh = await member.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = member.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var login = await member.Client.PostAsJsonAsync("/api/v1/auth/login", new { Email = member.Email, Password = "SecurePass123!" });
        login.EnsureSuccessStatusCode();
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("accessToken").GetString()!;
        Assert.True(string.IsNullOrEmpty(CoupleClaimOf(access)));
    }

    [Fact]
    public async Task TransactionsEnteredByTheLeaver_StayInTheGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, _, _) = await NewGroupOfThreeAsync(factory);
        var created = await member.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            Amount = 42m, Currency = "BRL", Description = "Feito pelo membro", Category = "Alimentação", EventTimestampUtc = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await member.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);

        var list = await owner.Client.GetFromJsonAsync<JsonElement>("/api/v1/transactions", Json);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Feito pelo membro", item.GetProperty("description").GetString());
    }

    [Fact]
    public async Task LastMemberLeaving_LeavesTheGroupUnreachable_AndItsDataIsKept()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var owner = await RegisterAsync(factory, "solo");
        var created = await owner.Client.PostAsJsonAsync("/api/v1/couples", new { });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        owner.Authenticate(createdBody.GetProperty("accessToken").GetString()!);
        var code = createdBody.GetProperty("joinCode").GetString()!;
        var coupleId = createdBody.GetProperty("coupleId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            Amount = 5m, Currency = "BRL", Description = "x", Category = "Alimentação", EventTimestampUtc = DateTime.UtcNow,
        })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);

        // The code of an empty group is dead: nobody can walk into the abandoned data.
        var newcomer = await RegisterAsync(factory, "newcomer");
        var join = await newcomer.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = code });
        Assert.Equal(HttpStatusCode.Gone, join.StatusCode);
        Assert.Equal("JOIN_CODE_EXPIRED", (await ErrorOf(join)).GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Set<Transaction>().IgnoreQueryFilters().CountAsync(t => t.CoupleId == coupleId));
    }

    [Fact]
    public async Task OnlyTheOwnerCanRemoveMembers()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, third, _) = await NewGroupOfThreeAsync(factory);

        var response = await member.Client.DeleteAsync($"/api/v1/couples/members/{third.UserId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("NOT_COUPLE_OWNER", (await ErrorOf(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await third.Client.GetAsync("/api/v1/transactions")).StatusCode);
    }

    [Fact]
    public async Task Owner_CannotRemoveThemselves_NorSomeoneOutsideTheGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, _, _, _) = await NewGroupOfThreeAsync(factory);
        var stranger = await RegisterAsync(factory, "stranger");

        var self = await owner.Client.DeleteAsync($"/api/v1/couples/members/{owner.UserId}");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal("CANNOT_REMOVE_SELF", (await ErrorOf(self)).GetProperty("code").GetString());

        var outside = await owner.Client.DeleteAsync($"/api/v1/couples/members/{stranger.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal("MEMBER_NOT_FOUND", (await ErrorOf(outside)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task WhenTheOwnerLeaves_OwnershipGoesToTheOldestRemainingMember()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, third, _) = await NewGroupOfThreeAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);

        var me = await member.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me", Json);
        Assert.Equal(member.UserId, me.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(2, me.GetProperty("members").GetArrayLength());
        // The new owner can now remove the other member.
        Assert.Equal(HttpStatusCode.NoContent, (await member.Client.DeleteAsync($"/api/v1/couples/members/{third.UserId}")).StatusCode);
    }

    [Fact]
    public async Task Me_ShowsOwnerCodeAndItsExpiry_ToEveryMember()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, _, code) = await NewGroupOfThreeAsync(factory);

        var me = await member.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me", Json);

        Assert.Equal(owner.UserId, me.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(code, me.GetProperty("joinCode").GetString());
        var expires = me.GetProperty("joinCodeExpiresAtUtc").GetDateTime();
        Assert.InRange(DateTime.SpecifyKind(expires, DateTimeKind.Utc), DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));
    }

    [Fact]
    public async Task NewCode_HasEightUnambiguousCharacters()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var owner = await RegisterAsync(factory, "owner");

        var created = await owner.Client.PostAsJsonAsync("/api/v1/couples", new { });
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("joinCode").GetString()!;

        Assert.Equal(8, code.Length);
        Assert.DoesNotContain(code, c => "0O1I".Contains(c));
    }

    [Fact]
    public async Task OwnerRenewsTheCode_TheOldOneStopsAtOnce_AndTheNewOneJoins()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, _, _, oldCode) = await NewGroupOfThreeAsync(factory);

        var renewed = await owner.Client.PostAsJsonAsync("/api/v1/couples/join-code", new { });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var body = await renewed.Content.ReadFromJsonAsync<JsonElement>(Json);
        var newCode = body.GetProperty("joinCode").GetString()!;
        Assert.NotEqual(oldCode, newCode);
        Assert.Equal(8, newCode.Length);
        Assert.InRange(DateTime.SpecifyKind(body.GetProperty("joinCodeExpiresAtUtc").GetDateTime(), DateTimeKind.Utc),
            DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));

        var late = await RegisterAsync(factory, "late");
        var withOld = await late.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = oldCode });
        Assert.Equal(HttpStatusCode.NotFound, withOld.StatusCode);
        Assert.Equal("COUPLE_NOT_FOUND", (await ErrorOf(withOld)).GetProperty("code").GetString());

        await JoinAsync(late, newCode);
    }

    [Fact]
    public async Task OnlyTheOwnerCanRenewTheCode()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (_, member, _, code) = await NewGroupOfThreeAsync(factory);

        var response = await member.Client.PostAsJsonAsync("/api/v1/couples/join-code", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("NOT_COUPLE_OWNER", (await ErrorOf(response)).GetProperty("code").GetString());
        var me = await member.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me", Json);
        Assert.Equal(code, me.GetProperty("joinCode").GetString());
    }

    [Fact]
    public async Task ExpiredCode_IsRefusedWithAClearMessage_AndLegacySixCharacterCodesWorkUntilThen()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, _, _, code) = await NewGroupOfThreeAsync(factory);
        // A group from before the 8-character format.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var couple = await db.Couples.SingleAsync(c => c.JoinCode == code);
            couple.RegenerateJoinCode("LEG123", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var early = await RegisterAsync(factory, "early");
        await JoinAsync(early, "leg123");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var couple = await db.Couples.SingleAsync(c => c.JoinCode == "LEG123");
            couple.RegenerateJoinCode("LEG123", DateTime.UtcNow.AddDays(-8));
            await db.SaveChangesAsync();
        }

        var late = await RegisterAsync(factory, "late");
        var response = await late.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "LEG123" });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("JOIN_CODE_EXPIRED", error.GetProperty("code").GetString());
        Assert.Contains("código", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("ABCDE")]
    [InlineData("ABCDEFG")]
    [InlineData("ABCDEFGHI")]
    public async Task Join_WithCodeOfOtherLength_IsAValidationError(string code)
    {
        await using var factory = new TransactionWebApplicationFactory();
        var actor = await RegisterAsync(factory, "typo");

        var response = await actor.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", (await ErrorOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task LeaverAndRemovedMember_StopReceivingTheGroupsAlerts()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member, third, _) = await NewGroupOfThreeAsync(factory);
        Guid coupleId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            coupleId = (await db.Users.SingleAsync(u => u.Id == owner.UserId)).CoupleId!.Value;
            foreach (var actor in new[] { member, third, owner })
            {
                db.DeviceTokens.Add(DeviceToken.Create(actor.UserId, coupleId, $"fcm-{actor.UserId:N}", DateTime.UtcNow));
                db.NotificationEvents.Add(NotificationEvent.Create(coupleId, actor.UserId, "LargeTransaction", "t", "b", DateTime.UtcNow));
            }
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await third.Client.PostAsJsonAsync("/api/v1/couples/leave", new { })).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokens = await db.DeviceTokens.IgnoreQueryFilters().Select(d => d.UserId).ToListAsync();
            Assert.Equal([owner.UserId], tokens);
            var events = await db.NotificationEvents.IgnoreQueryFilters().Where(e => e.AlertType == "LargeTransaction").ToListAsync();
            Assert.All(events.Where(e => e.UserId != owner.UserId), e => Assert.Equal("Failed", e.Status));
            Assert.Equal("Pending", events.Single(e => e.UserId == owner.UserId).Status);
        }
    }

    [Fact]
    public async Task AfterLeaving_TheUserCanStartOrJoinAnotherGroup()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (_, member, _, _) = await NewGroupOfThreeAsync(factory);
        var leave = await member.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        var body = await leave.Content.ReadFromJsonAsync<JsonElement>(Json);
        member.Authenticate(body.GetProperty("accessToken").GetString()!);

        var created = await member.Client.PostAsJsonAsync("/api/v1/couples", new { });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        member.Authenticate(createdBody.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await member.Client.GetAsync("/api/v1/transactions")).StatusCode);
        var me = await member.Client.GetFromJsonAsync<JsonElement>("/api/v1/couples/me", Json);
        Assert.Equal(member.UserId, me.GetProperty("ownerUserId").GetGuid());
    }

    [Fact]
    public async Task LeaveWithoutAGroup_Returns404InTheSingleErrorFormat()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var actor = await RegisterAsync(factory, "alone");

        var response = await actor.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("COUPLE_NOT_FOUND", (await ErrorOf(response)).GetProperty("code").GetString());
    }
}
