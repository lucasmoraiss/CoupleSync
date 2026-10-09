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
/// follows what is really configured: Groq is listed only while its key exists and a chain has a link of it. And the
/// AI text has a new version: a group whose acceptance is of the version that named only Google is asked again, and
/// until someone accepts nothing is sent. No test talks to a real provider; the key is invented.
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
    public async Task AGroupThatAcceptedVersion1_IsAskedAgain_NothingIsSentMeanwhile_AndTheNewAcceptanceIsVersion2()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);

        // Ana accepted the old text: the question comes back to her. The group is off for both.
        var anaStatus = await StatusOf(ana.Client);
        Assert.Equal(2, anaStatus.GetProperty("consentVersion").GetInt32());
        Assert.False(anaStatus.GetProperty("enabled").GetBoolean());
        Assert.True(anaStatus.GetProperty("onboardingPending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, anaStatus.GetProperty("myAcceptance").ValueKind);
        Assert.Equal(0, anaStatus.GetProperty("acceptedBy").GetArrayLength());

        // Bruno never accepted and had answered "Entendi": nobody "switched it on" under the text in force, so he is
        // not told that Ana did, and he is not asked.
        var brunoStatus = await StatusOf(bruno.Client);
        Assert.False(brunoStatus.GetProperty("enabled").GetBoolean());
        Assert.False(brunoStatus.GetProperty("onboardingPending").GetBoolean());
        Assert.Equal(0, brunoStatus.GetProperty("acceptedBy").GetArrayLength());

        // Nothing leaves for any provider.
        foreach (var member in new[] { ana, bruno })
        {
            var chat = await member.Client.PostAsJsonAsync(Chat, new { Message = "Quanto gastamos?", History = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
            Assert.Equal("AI_CONSENT_REQUIRED", (await ErrorOf(chat)).Code);
        }

        Assert.Empty(factory.UsageRows());

        // The installed app (without the update) still sends version 1: refused, in Portuguese, nothing stored.
        var old = await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        var error = await ErrorOf(old);
        Assert.Equal("AI_CONSENT_VERSION_OUTDATED", error.Code);
        Assert.Contains("Atualize o app", error.Message);
        Assert.False((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 2"));

        // The updated app accepts version 2.
        var accepted = await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 2 AND revoked_at_utc IS NULL"));

        anaStatus = await StatusOf(ana.Client);
        Assert.True(anaStatus.GetProperty("enabled").GetBoolean());
        Assert.False(anaStatus.GetProperty("onboardingPending").GetBoolean());

        // Now someone did switch it on under the text in force: Bruno is told, with her name.
        brunoStatus = await StatusOf(bruno.Client);
        Assert.True(brunoStatus.GetProperty("enabled").GetBoolean());
        Assert.True(brunoStatus.GetProperty("onboardingPending").GetBoolean());
        Assert.Equal("Ana Exemplo", Assert.Single(brunoStatus.GetProperty("acceptedBy").EnumerateArray()).GetProperty("name").GetString());

        var answered = await bruno.Client.PostAsJsonAsync(Chat, new { Message = "Quanto gastamos?", History = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Single(factory.UsageRows());
    }

    [Fact]
    public async Task AnsweringNotNow_ToTheNewText_EndsTheOldAcceptance_AndTheQuestionDoesNotComeBack()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);
        Assert.True((await StatusOf(ana.Client)).GetProperty("onboardingPending").GetBoolean());

        var notNow = await ana.Client.PatchAsync(Preferences, JsonContent.Create(new { OnboardingAnswered = true }));
        Assert.Equal(HttpStatusCode.OK, notNow.StatusCode);

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            Assert.False(status.GetProperty("enabled").GetBoolean());
        }

        // The old acceptance is over (revoked by her), not erased.
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 1 AND revoked_at_utc IS NOT NULL"));
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL"));
    }

    [Fact]
    public async Task SwitchingItOffForTheGroup_AlsoEndsAnOldAcceptance()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await GroupThatAcceptedTheOldVersionAsync(factory);

        var revoked = await bruno.Client.DeleteAsync($"{Consent}?scope=group");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        Assert.False((await StatusOf(ana.Client)).GetProperty("onboardingPending").GetBoolean());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL"));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Member(HttpClient Client, Guid UserId);

    private sealed record ErrorDto(string Code, string Message);

    private static async Task<ErrorDto> ErrorOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorDto>())!;

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
