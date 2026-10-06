using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.IntegrationTests.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.Auth;

/// <summary>Server logout, authenticated password change, the new-password rule, login messages, and sessions after a group change.</summary>
[Trait("Category", "SessionAndPassword")]
public sealed class SessionAndPasswordIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string StrongPassword = "SecurePass123!";

    private sealed record Session(HttpClient Client, Guid UserId, string Email, string AccessToken, string RefreshToken);

    private static async Task<Session> RegisterAsync(TransactionWebApplicationFactory factory, string label = "user")
    {
        var client = factory.CreateClient();
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = label, Password = StrongPassword });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var session = new Session(
            client,
            body.GetProperty("user").GetProperty("id").GetGuid(),
            email,
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return session;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = refreshToken });

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    // logout

    [Fact]
    public async Task Logout_RevokesTheRefreshToken_AndIsIdempotent()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var logout = await session.Client.PostAsJsonAsync("/api/v1/auth/logout", new { session.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.Client, session.RefreshToken)).StatusCode);

        // Same token again, and a token that never existed: same answer, nothing revealed.
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsJsonAsync("/api/v1/auth/logout", new { session.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsJsonAsync("/api/v1/auth/logout", new { RefreshToken = "never-existed" })).StatusCode);
    }

    [Fact]
    public async Task Logout_WorksWithoutAnAccessToken()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);
        using var anonymous = factory.CreateClient();

        var logout = await anonymous.PostAsJsonAsync("/api/v1/auth/logout", new { session.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(anonymous, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutToken_IsAValidationError()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/logout", new { RefreshToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logout_OfOneUser_DoesNotAffectAnother()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var first = await RegisterAsync(factory, "first");
        var second = await RegisterAsync(factory, "second");

        await first.Client.PostAsJsonAsync("/api/v1/auth/logout", new { first.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(second.Client, second.RefreshToken)).StatusCode);
    }

    // change password

    [Fact]
    public async Task ChangePassword_WithCorrectCurrentPassword_SwapsThePassword_AndInvalidatesOtherRefreshTokens()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var response = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = StrongPassword, NewPassword = "NovaSenha456" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));

        // The refresh token held before (any other device) is dead; the one returned works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.Client, session.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.Client, newRefresh)).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { session.Email, Password = StrongPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { session.Email, Password = "NovaSenha456" })).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_IsRejected_AndChangesNothing()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var response = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = "SenhaErrada999", NewPassword = "NovaSenha456" });

        // 400, not 401: the session is valid, so the app must not treat it as an expired session.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("INVALID_CURRENT_PASSWORD", body.GetProperty("code").GetString());
        Assert.Equal("A senha atual está incorreta.", body.GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.Client, session.RefreshToken)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new { session.Email, Password = StrongPassword })).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RequiresAuthentication()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = StrongPassword, NewPassword = "NovaSenha456" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("curta1", "A senha precisa ter pelo menos 8 caracteres.")]
    [InlineData("somenteletras", "A senha precisa ter pelo menos um número.")]
    [InlineData("12345678901", "A senha precisa ter pelo menos uma letra.")]
    [InlineData("senha123", "Essa senha é muito comum. Escolha outra.")]
    public async Task ChangePassword_NewPasswordBreakingTheRule_SaysWhatIsMissing(string newPassword, string expectedMessage)
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var response = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = StrongPassword, NewPassword = newPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        var messages = body.GetProperty("errors").GetProperty("NewPassword").EnumerateArray().Select(m => m.GetString()).ToArray();
        Assert.Contains(expectedMessage, messages);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.Client, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NewPasswordEqualToTheEmail_IsRejected()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory, "emailpass");

        var response = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = StrongPassword, NewPassword = session.Email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("A senha não pode ser igual ao seu e-mail.",
            (await BodyOf(response)).GetProperty("errors").GetProperty("NewPassword").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task ChangePassword_NewPasswordEqualToTheCurrent_IsRejected()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var response = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = StrongPassword, NewPassword = StrongPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("A nova senha precisa ser diferente da atual.",
            (await BodyOf(response)).GetProperty("errors").GetProperty("NewPassword").EnumerateArray().Select(m => m.GetString()));
    }

    // registration rule

    [Theory]
    [InlineData("12345678", "A senha precisa ter pelo menos uma letra.")]
    [InlineData("abcdefgh", "A senha precisa ter pelo menos um número.")]
    [InlineData("ab12", "A senha precisa ter pelo menos 8 caracteres.")]
    [InlineData("password123", "Essa senha é muito comum. Escolha outra.")]
    public async Task Register_PasswordBreakingTheRule_SaysWhatIsMissing(string password, string expectedMessage)
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = $"rule-{Guid.NewGuid():N}@example.com", Name = "Rule", Password = password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.Contains(expectedMessage, body.GetProperty("errors").GetProperty("Password").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task Register_PasswordEqualToTheEmail_IsRejected()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        var email = $"same-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = "Same", Password = email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("A senha não pode ser igual ao seu e-mail.",
            (await BodyOf(response)).GetProperty("errors").GetProperty("Password").EnumerateArray().Select(m => m.GetString()));
    }

    // login: same message whether or not the e-mail exists

    [Fact]
    public async Task Login_UnknownEmail_AndWrongPassword_AnswerIdentically()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);
        using var client = factory.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = $"ghost-{Guid.NewGuid():N}@example.com", Password = StrongPassword });
        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { session.Email, Password = "SenhaErrada999" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var unknownBody = await BodyOf(unknown);
        var wrongBody = await BodyOf(wrong);
        Assert.Equal("INVALID_CREDENTIALS", unknownBody.GetProperty("code").GetString());
        Assert.Equal(wrongBody.GetProperty("code").GetString(), unknownBody.GetProperty("code").GetString());
        Assert.Equal(wrongBody.GetProperty("message").GetString(), unknownBody.GetProperty("message").GetString());
    }

    // sessions after leaving / being removed from a group

    [Fact]
    public async Task RemovedMember_CreatingAGroup_GetsASessionThatKeepsRenewing()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member) = await GroupOfTwoAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(member.Client, member.RefreshToken)).StatusCode);

        var created = await member.Client.PostAsJsonAsync("/api/v1/couples", new { });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await BodyOf(created);
        var refresh = body.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(refresh));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(member.Client, refresh!)).StatusCode);
    }

    [Fact]
    public async Task RemovedMember_JoiningAGroup_GetsASessionThatKeepsRenewing()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member) = await GroupOfTwoAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/couples/members/{member.UserId}")).StatusCode);

        var other = await RegisterAsync(factory, "other");
        var otherGroup = await other.Client.PostAsJsonAsync("/api/v1/couples", new { });
        var code = (await BodyOf(otherGroup)).GetProperty("joinCode").GetString();

        var join = await member.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = code });

        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        var refresh = (await BodyOf(join)).GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(refresh));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(member.Client, refresh!)).StatusCode);
    }

    [Fact]
    public async Task UserWithAWorkingSession_CreatingAGroup_KeepsTheirCurrentRefreshToken()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var created = await session.Client.PostAsJsonAsync("/api/v1/couples", new { });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // Nothing new is issued (an installed app that ignores the field keeps working with the token it has).
        Assert.Equal(JsonValueKind.Null, (await BodyOf(created)).GetProperty("refreshToken").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.Client, session.RefreshToken)).StatusCode);
    }

    // fix round 1

    [Fact]
    public async Task Logout_WithDeviceToken_DeletesThatDevicesRegistration_ForTheTokenOwnerOnly()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, member) = await GroupOfTwoAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var coupleId = (await db.Users.SingleAsync(u => u.Id == owner.UserId)).CoupleId!.Value;
            db.DeviceTokens.Add(DeviceToken.Create(owner.UserId, coupleId, "fcm-owner", DateTime.UtcNow));
            db.DeviceTokens.Add(DeviceToken.Create(member.UserId, coupleId, "fcm-member", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        // The member signs out claiming the OWNER device token: ignored (still 204), nothing of the owner is touched.
        var spoof = await member.Client.PostAsJsonAsync("/api/v1/auth/logout",
            new { member.RefreshToken, DeviceToken = "fcm-owner" });
        Assert.Equal(HttpStatusCode.NoContent, spoof.StatusCode);
        Assert.Equal(new[] { "fcm-member", "fcm-owner" }, await DeviceTokensAsync(factory));

        var owners = await owner.Client.PostAsJsonAsync("/api/v1/auth/logout",
            new { owner.RefreshToken, DeviceToken = "fcm-owner" });
        Assert.Equal(HttpStatusCode.NoContent, owners.StatusCode);
        Assert.Equal(new[] { "fcm-member" }, await DeviceTokensAsync(factory));
    }

    [Fact]
    public async Task Logout_WithAnUnknownRefreshToken_NeverDeletesADeviceToken()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var (owner, _) = await GroupOfTwoAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var coupleId = (await db.Users.SingleAsync(u => u.Id == owner.UserId)).CoupleId!.Value;
            db.DeviceTokens.Add(DeviceToken.Create(owner.UserId, coupleId, "fcm-owner", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var response = await owner.Client.PostAsJsonAsync("/api/v1/auth/logout",
            new { RefreshToken = "never-existed", DeviceToken = "fcm-owner" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { "fcm-owner" }, await DeviceTokensAsync(factory));
    }

    [Fact]
    public async Task Logout_AndRefresh_RejectAnOversizedToken()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        var huge = new string('x', 513);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { RefreshToken = huge });
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = huge });
        var hugeDevice = await client.PostAsJsonAsync("/api/v1/auth/logout", new { RefreshToken = "abc", DeviceToken = huge });

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hugeDevice.StatusCode);
    }

    [Fact]
    public async Task Register_EmptyPassword_ReportsOnlyThatItIsRequired()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = $"empty-{Guid.NewGuid():N}@example.com", Name = "Empty", Password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await BodyOf(response)).GetProperty("errors").GetProperty("Password").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task ChangePassword_EmptyNewPassword_ReportsOnlyThatItIsRequired()
    {
        await using var factory = new TransactionWebApplicationFactory();
        var session = await RegisterAsync(factory);

        var response = await session.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { CurrentPassword = StrongPassword, NewPassword = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await BodyOf(response)).GetProperty("errors").GetProperty("NewPassword").EnumerateArray().ToArray());
    }

    private static async Task<string[]> DeviceTokensAsync(TransactionWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.DeviceTokens.IgnoreQueryFilters().Select(d => d.Token).ToListAsync()).Order().ToArray();
    }

    private static async Task<(Session Owner, Session Member)> GroupOfTwoAsync(TransactionWebApplicationFactory factory)
    {
        var owner = await RegisterAsync(factory, "owner");
        var created = await owner.Client.PostAsJsonAsync("/api/v1/couples", new { });
        created.EnsureSuccessStatusCode();
        var createdBody = await BodyOf(created);
        var code = createdBody.GetProperty("joinCode").GetString();
        owner.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", createdBody.GetProperty("accessToken").GetString());

        var member = await RegisterAsync(factory, "member");
        var join = await member.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = code });
        join.EnsureSuccessStatusCode();
        member.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await BodyOf(join)).GetProperty("accessToken").GetString());
        return (owner, member);
    }
}
