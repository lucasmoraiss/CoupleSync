using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.AiChat;

/// <summary>
/// Groq as the second provider (design 2.2 and 7.3, decision 15), through HTTP on SQLite. What the person is told
/// follows what is really configured: Groq is listed only while its key exists and a chain has a link of it. The AI
/// text has a version 2 that names Groq, but no version switches a group off: a group whose only acceptance is of
/// the version that named only Google is on like any other, and nobody is asked again. No test talks to a real
/// provider; the key is invented.
/// </summary>
[Trait("Category", "AiChat")]
public sealed class AiSecondProviderTests
{
    private const string Status = "/api/v1/ai/status";
    private const string Consent = "/api/v1/ai/consent";
    private const string Preferences = "/api/v1/ai/preferences";
    private const string Chat = "/api/v1/ai/chat";
    private const string FakeKey = "fake-key-not-real-0a1b2c";

    private const string Google = """{"name":"Google (Gemini)","country":"Estados Unidos","trainsOnData":true}""";
    private const string Groq = """{"name":"Groq","country":"Estados Unidos","trainsOnData":false}""";

    // ---------------------------------------------------------------- who is listed

    [Fact]
    public async Task WithoutTheGroqKey_TheStatusListsOnlyGoogle_ByteForByteAsBefore()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true, useRealCatalog: true);
        var ana = await OwnerAsync(factory, "Ana Exemplo");

        var status = await StatusOf(ana.Member.Client);

        Assert.Equal($"[{Google}]", status.GetProperty("providers").GetRawText());
        Assert.True(status.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task WithTheGroqKey_TheStatusListsGoogleAndThenGroq_WhichDoesNotTrainOnTheData()
    {
        await using var factory = new ChatWebApplicationFactory(
            enabled: true, useRealCatalog: true, config: new() { ["GROQ_API_KEY"] = FakeKey });
        var ana = await OwnerAsync(factory, "Ana Exemplo");

        var status = await StatusOf(ana.Member.Client);

        Assert.Equal($"[{Google},{Groq}]", status.GetProperty("providers").GetRawText());
    }

    [Fact]
    public async Task WithTheGroqKeyButNoGroqLinkInAnyChain_GroqReceivesNothing_SoItIsNotListed()
    {
        var config = new Dictionary<string, string?> { ["GROQ_API_KEY"] = FakeKey };
        foreach (var chain in new[] { "Assistant", "Weekly", "Daily", "Categorize", "Education" })
            config[$"Ai:Chains:{chain}:0"] = "gemini|gemini-flash-latest";
        await using var factory = new ChatWebApplicationFactory(enabled: true, useRealCatalog: true, config: config);
        var ana = await OwnerAsync(factory, "Ana Exemplo");

        Assert.Equal($"[{Google}]", (await StatusOf(ana.Member.Client)).GetProperty("providers").GetRawText());
    }

    // ---------------------------------------------------------------- the new version of the text

    [Fact]
    public async Task AGroupThatAcceptedVersion1_IsOn_NobodyIsAskedAgain_AndActivatingWithVersion1IsNotRefused()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);

        // The group is on for both and nobody is asked. The row of version 1 is not an acceptance of the text in
        // force, so it is not listed as one — for Ana or for Bruno.
        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.Equal(2, status.GetProperty("consentVersion").GetInt32());
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("myAcceptance").ValueKind);
            Assert.Equal(0, status.GetProperty("acceptedBy").GetArrayLength());

            var chat = await member.Client.PostAsJsonAsync(Chat, new { Message = "Quanto gastamos?", History = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        }

        Assert.Equal(2, factory.UsageRows().Count);

        // An app that still sends version 1: 200, never AI_CONSENT_VERSION_OUTDATED. No acceptance of the text in
        // force is written for a version that is not it, and the row of version 1 stays as it was.
        var old = await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 });
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.True((await old.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 2"));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NULL"));

        // The installed app sends version 2: stored as her acceptance, listed to both, and still nobody is asked.
        var accepted = await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 2 AND revoked_at_utc IS NULL"));

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            Assert.Equal("Ana Exemplo", Assert.Single(status.GetProperty("acceptedBy").EnumerateArray()).GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task AnsweringNotNow_ToTheNewText_EndsTheOldAcceptance_AndTheGroupStaysOn()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);

        var notNow = await ana.Client.PatchAsync(Preferences, JsonContent.Create(new { OnboardingAnswered = true }));
        Assert.Equal(HttpStatusCode.OK, notNow.StatusCode);

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            // "Not now" from an app that still shows the old question is not "off for the group".
            Assert.True(status.GetProperty("enabled").GetBoolean());
        }

        // The old acceptance is over (revoked by her), not erased.
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NOT NULL"));
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL"));
    }

    [Fact]
    public async Task AnsweringNotNow_EndsOnlyTheOldAcceptanceOfWhoAnswered_NotTheOtherMembersNorAnotherGroups()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);
        var group = Assert.Single(factory.CoupleIds());

        // Bruno of the same group also has an active acceptance of version 1; so does Carla, of ANOTHER group.
        var (carla, _) = await OwnerAsync(factory, "Carla Exemplo");
        var otherGroup = Assert.Single(factory.CoupleIds(), id => id != group);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiConsents.Add(AiConsent.Accept(group, bruno.UserId, version: 1, DateTime.UtcNow.AddHours(-2)));
            db.AiConsents.Add(AiConsent.Accept(otherGroup, carla.UserId, version: 1, DateTime.UtcNow.AddHours(-2)));
            await db.SaveChangesAsync();
        }

        var notNow = await ana.Client.PatchAsync(Preferences, JsonContent.Create(new { OnboardingAnswered = true }));
        Assert.Equal(HttpStatusCode.OK, notNow.StatusCode);

        long Revoked(Guid userId, bool revoked) => factory.Scalar<long>(
            $"SELECT count(*) FROM ai_consents WHERE version = 1 AND upper(user_id) = '{userId.ToString().ToUpperInvariant()}' "
            + $"AND revoked_at_utc IS {(revoked ? "NOT NULL" : "NULL")}");

        // Only her own row is over.
        Assert.Equal(1, Revoked(ana.UserId, revoked: true));
        Assert.Equal(0, Revoked(ana.UserId, revoked: false));
        // Bruno (same group) and Carla (another group) keep theirs.
        Assert.Equal(1, Revoked(bruno.UserId, revoked: false));
        Assert.Equal(0, Revoked(bruno.UserId, revoked: true));
        Assert.Equal(1, Revoked(carla.UserId, revoked: false));
        Assert.Equal(0, Revoked(carla.UserId, revoked: true));

        // Nobody is asked, and both groups are on.
        foreach (var member in new[] { ana, bruno, carla })
        {
            var status = await StatusOf(member.Client);
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            Assert.True(status.GetProperty("enabled").GetBoolean());
        }
    }

    [Fact]
    public async Task AnsweringNotNow_AfterAcceptingTheNewText_EndsOnlyTheOldRow_AndTheNewAcceptanceStays()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, _) = await GroupThatAcceptedTheOldVersionAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NULL"));

        // A preference that is not the welcome answer revokes nothing.
        var weekly = await ana.Client.PatchAsync(Preferences, JsonContent.Create(new { WeeklyEmail = false }));
        Assert.Equal(HttpStatusCode.OK, weekly.StatusCode);
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NULL"));

        // The answer of an old screen ("not now" / "entendi") after she already accepted the text in force.
        var answered = await ana.Client.PatchAsync(Preferences, JsonContent.Create(new { OnboardingAnswered = true }));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 2 AND revoked_at_utc IS NULL"));
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NULL"));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NOT NULL"));
        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task SwitchingItOffForTheGroup_SwitchesItOff_AndAlsoEndsAnOldAcceptance()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);

        var revoked = await bruno.Client.DeleteAsync($"{Consent}?scope=group");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        Assert.False((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        Assert.False((await StatusOf(ana.Client)).GetProperty("onboardingPending").GetBoolean());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL"));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Member(HttpClient Client, Guid UserId);

    private static async Task<JsonElement> StatusOf(HttpClient client)
    {
        var response = await client.GetAsync(Status);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Ana and Bruno as they are in production after the previous version: both answered the welcome question, and
    /// the acceptance of Ana is a row of version 1 (written straight into the table: no route stores an old version).
    /// </summary>
    private static async Task<(Member Ana, Member Bruno)> GroupThatAcceptedTheOldVersionAsync(ChatWebApplicationFactory factory)
    {
        var (ana, joinCode) = await OwnerAsync(factory, "Ana Exemplo");
        var bruno = await RegisterAsync(factory, "Bruno Exemplo");
        var joined = await bruno.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var body = await joined.Content.ReadFromJsonAsync<JsonElement>();
        bruno.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        foreach (var member in new[] { ana, bruno })
        {
            var answered = await member.Client.PatchAsync(Preferences, JsonContent.Create(new { OnboardingAnswered = true }));
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiConsents.Add(AiConsent.Accept(Assert.Single(factory.CoupleIds()), ana.UserId, version: 1, DateTime.UtcNow.AddHours(-2)));
        await db.SaveChangesAsync();

        return (ana, bruno);
    }

    private static async Task<Member> RegisterAsync(ChatWebApplicationFactory factory, string name)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register", new { Email = $"ai-{Guid.NewGuid():N}@example.com", Name = name, Password = "SecurePass123!" });
        register.EnsureSuccessStatusCode();
        var body = await register.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return new Member(client, body.GetProperty("user").GetProperty("id").GetGuid());
    }

    private static async Task<(Member Member, string JoinCode)> OwnerAsync(ChatWebApplicationFactory factory, string name)
    {
        var member = await RegisterAsync(factory, name);
        var created = await member.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        member.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return (member, body.GetProperty("joinCode").GetString()!);
    }
}
