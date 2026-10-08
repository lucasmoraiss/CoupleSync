using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleSync.IntegrationTests.AiChat;

/// <summary>
/// Issue #38 — switching the AI on for a group (design 7 and 10.2): one acceptance counts for the whole group and is
/// stored on the server as "switched on by X"; any member switches it off; who leaves stops counting; the chat only
/// answers a group that switched it on. Everything through HTTP, on SQLite, with stub providers; the rows are read
/// with raw SQL.
/// </summary>
[Trait("Category", "AiChat")]
public sealed class AiActivationTests
{
    private const string Chat = "/api/v1/ai/chat";
    private const string Status = "/api/v1/ai/status";
    private const string Consent = "/api/v1/ai/consent";
    private const string Preferences = "/api/v1/ai/preferences";
    private const string Usage = "/api/v1/ai/usage";

    private static object Question() => new { Message = "Quais são meus gastos do mês?", History = (object?)null };

    // ---------------------------------------------------------------- switching on

    [Fact]
    public async Task OneMemberAccepts_AndBothMembersSeeTheGroupEnabled_WithTheNameAndTheDateOfWhoSwitchedItOn()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");

        var before = await StatusOf(bruno.Client);
        Assert.True(before.GetProperty("available").GetBoolean());
        Assert.False(before.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, before.GetProperty("acceptedBy").GetArrayLength());

        var askedAt = DateTime.UtcNow.AddSeconds(-5);
        var accepted = await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.Equal(1, status.GetProperty("consentVersion").GetInt32());
            var by = Assert.Single(status.GetProperty("acceptedBy").EnumerateArray());
            Assert.Equal(ana.UserId, by.GetProperty("userId").GetGuid());
            Assert.Equal("Ana Exemplo", by.GetProperty("name").GetString());
            Assert.InRange(by.GetProperty("acceptedAtUtc").GetDateTime().ToUniversalTime(), askedAt, DateTime.UtcNow.AddSeconds(5));
        }

        Assert.Equal(JsonValueKind.Object, (await StatusOf(ana.Client)).GetProperty("myAcceptance").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await StatusOf(bruno.Client)).GetProperty("myAcceptance").ValueKind);
    }

    [Fact]
    public async Task TheStatus_CarriesEveryFieldOfTheContract()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var status = await StatusOf(ana.Client);

        foreach (var field in new[]
                 {
                     "available", "enabled", "consentVersion", "acceptedBy", "myAcceptance", "onboardingPending", "weeklyEmailEnabled",
                     "emailVerified", "emailConfigured", "providers", "features", "budget",
                 })
        {
            Assert.True(status.TryGetProperty(field, out _), $"the status should carry '{field}'");
        }

        var provider = Assert.Single(status.GetProperty("providers").EnumerateArray());
        Assert.Equal("Google (Gemini)", provider.GetProperty("name").GetString());
        Assert.Equal("Estados Unidos", provider.GetProperty("country").GetString());
        Assert.True(provider.GetProperty("trainsOnData").GetBoolean());
        Assert.True(status.GetProperty("features").GetProperty("assistant").GetBoolean());
        Assert.False(status.GetProperty("features").GetProperty("insights").GetBoolean());
        Assert.Equal(0, status.GetProperty("budget").GetProperty("callsToday").GetInt32());
        Assert.Equal(25, status.GetProperty("budget").GetProperty("callLimit").GetInt32());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T00:00:00$", status.GetProperty("budget").GetProperty("resetsAtLocal").GetString()!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task AcceptingAVersionThatIsNotTheCurrentOne_Is409AiConsentVersionOutdated_AndNothingIsStored(int version)
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var response = await ana.Client.PostAsJsonAsync(Consent, new { Version = version });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("AI_CONSENT_VERSION_OUTDATED", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
    }

    [Fact]
    public async Task WithNoProviderKey_TheAiIsNotAvailable_AndAcceptingIs503AiUnavailable()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true, withGeminiKey: false, useRealCatalog: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var status = await StatusOf(ana.Client);
        Assert.False(status.GetProperty("available").GetBoolean());
        // Nothing to ask while there is no AI: the welcome screen does not come up.
        Assert.False(status.GetProperty("onboardingPending").GetBoolean());

        var response = await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("AI_UNAVAILABLE", (await ErrorOf(response)).Code);
    }

    [Fact]
    public async Task WithTheFakeProviderOn_AndNoKey_TheAiIsAvailable()
    {
        await using var factory = new ChatWebApplicationFactory(
            enabled: true, withGeminiKey: false, useRealCatalog: true, config: new() { ["Ai:UseFakeProvider"] = "true" });
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var status = await StatusOf(ana.Client);

        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.True(status.GetProperty("onboardingPending").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);
    }

    // ---------------------------------------------------------------- switching off

    [Fact]
    public async Task ScopeGroup_ByAnyMember_RevokesEveryAcceptance_AndRecordsWhoDidIt()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        await AcceptAsync(ana.Client);
        await AcceptAsync(bruno.Client);
        Assert.Equal(2, (await StatusOf(ana.Client)).GetProperty("acceptedBy").GetArrayLength());

        var response = await bruno.Client.DeleteAsync($"{Consent}?scope=group");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        Assert.False((await StatusOf(bruno.Client)).GetProperty("enabled").GetBoolean());
        Assert.Equal(2, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NOT NULL"));
        Assert.Equal(2, factory.Scalar<long>($"SELECT count(*) FROM ai_consents WHERE upper(revoked_by_user_id) = '{bruno.UserId.ToString().ToUpperInvariant()}'"));
    }

    [Fact]
    public async Task ScopeMine_RevokesOnlyTheOwnAcceptance_AndTheGroupGoesOffWhenNoneIsLeft()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        await AcceptAsync(ana.Client);
        await AcceptAsync(bruno.Client);

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);

        var afterAna = await StatusOf(ana.Client);
        Assert.True(afterAna.GetProperty("enabled").GetBoolean());
        Assert.Equal(bruno.UserId, Assert.Single(afterAna.GetProperty("acceptedBy").EnumerateArray()).GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, afterAna.GetProperty("myAcceptance").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);

        Assert.False((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("?scope=x")]
    [InlineData("?scope=")]
    [InlineData("")]
    public async Task AnUnknownScope_Is400InvalidScope_AndNothingIsRevoked(string query)
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        await AcceptAsync(ana.Client);

        var response = await ana.Client.DeleteAsync(Consent + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_SCOPE", (await ErrorOf(response)).Code);
        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task AcceptingAgainAfterRevoking_UpdatesTheSameRow_WithANewDate_AndNoSecondRow()
    {
        var start = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var clock = new MovingClock(start);
        await using var factory = new ChatWebApplicationFactory(enabled: true, configureServices: clock.Register);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        await AcceptAsync(ana.Client);
        var id = factory.Scalar<string>("SELECT id FROM ai_consents");
        var firstDate = factory.Scalar<string>("SELECT accepted_at_utc FROM ai_consents");

        clock.UtcNow = clock.UtcNow.AddHours(2);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NOT NULL"));

        clock.UtcNow = clock.UtcNow.AddHours(2);
        await AcceptAsync(ana.Client);

        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(id, factory.Scalar<string>("SELECT id FROM ai_consents"));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NULL AND revoked_by_user_id IS NULL"));
        Assert.NotEqual(firstDate, factory.Scalar<string>("SELECT accepted_at_utc FROM ai_consents"));
        var status = await StatusOf(ana.Client);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(
            start.AddHours(4),
            status.GetProperty("myAcceptance").GetProperty("acceptedAtUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task AcceptingTwice_KeepsOneRowAndTheFirstDate()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        await AcceptAsync(ana.Client);
        var firstDate = factory.Scalar<string>("SELECT accepted_at_utc FROM ai_consents");
        await AcceptAsync(ana.Client);

        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(firstDate, factory.Scalar<string>("SELECT accepted_at_utc FROM ai_consents"));
    }

    // ---------------------------------------------------------------- who leaves the group

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheMemberWhoLeavesOrIsRemoved_StopsCounting_GetsRevokedAt_LosesTheirPreferences_AndTheGroupGoesOff(bool removedByTheOwner)
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        // Only Bruno accepted; Ana answered "not now", so she has preferences of her own that must stay.
        await AcceptAsync(bruno.Client);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, new { OnboardingAnswered = true })).StatusCode);
        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        Assert.Equal(2, factory.Scalar<long>("SELECT count(*) FROM ai_user_preferences"));

        var exit = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsync("/api/v1/couples/leave", null);
        Assert.True(exit.IsSuccessStatusCode, $"the exit answered {(int)exit.StatusCode}");

        var status = await StatusOf(ana.Client);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, status.GetProperty("acceptedBy").GetArrayLength());
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NOT NULL"));
        Assert.Equal(0, factory.Scalar<long>($"SELECT count(*) FROM ai_user_preferences WHERE upper(user_id) = '{bruno.UserId.ToString().ToUpperInvariant()}'"));
        Assert.Equal(1, factory.Scalar<long>($"SELECT count(*) FROM ai_user_preferences WHERE upper(user_id) = '{ana.UserId.ToString().ToUpperInvariant()}'"));

        // And the chat of who stayed is refused without reaching a provider.
        var chat = await ana.Client.PostAsJsonAsync(Chat, Question());
        Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
        Assert.Empty(factory.UsageRows());
    }

    [Fact]
    public async Task WhenWhoLeavesWasNotTheOnlyAcceptance_TheGroupStaysOn()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        await AcceptAsync(ana.Client);
        await AcceptAsync(bruno.Client);

        Assert.True((await bruno.Client.PostAsync("/api/v1/couples/leave", null)).IsSuccessStatusCode);

        var status = await StatusOf(ana.Client);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(ana.UserId, Assert.Single(status.GetProperty("acceptedBy").EnumerateArray()).GetProperty("userId").GetGuid());
    }

    // ---------------------------------------------------------------- the chat

    [Fact]
    public async Task TheChat_InAGroupThatDidNotAccept_Is403AiConsentRequired_AndNoProviderIsCalled()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var response = await ana.Client.PostAsJsonAsync(Chat, Question());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("AI_CONSENT_REQUIRED", error.Code);
        Assert.Contains("Inteligência artificial", error.Message);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(factory.UsageRows());
    }

    [Fact]
    public async Task WithAiDisabled_TheStatusSaysNotAvailable_AndTheChatIs404AiChatDisabled_EvenForAGroupThatAccepted()
    {
        var database = $"couplesync-ai-disabled-{Guid.NewGuid():N}";
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var on = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider), databaseName: database);
        var ana = await MemberWithGroupAsync(on, "Ana Exemplo");
        await AcceptAsync(ana.Client);

        // The same database, now with the emergency switch: Ai__Disabled=true.
        await using var off = new ChatWebApplicationFactory(
            enabled: true, catalog: new StubCatalog(provider), databaseName: database, config: new() { ["Ai:Disabled"] = "true" });
        using var client = off.CreateClient();
        client.DefaultRequestHeaders.Authorization = ana.Client.DefaultRequestHeaders.Authorization;

        var status = await StatusOf(client);
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.False(status.GetProperty("features").GetProperty("assistant").GetBoolean());
        // The acceptance is still there: switching the server back on needs no new question.
        Assert.True(status.GetProperty("enabled").GetBoolean());

        var response = await client.PostAsJsonAsync(Chat, Question());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("AI_CHAT_DISABLED", (await ErrorOf(response)).Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AnAcceptanceRevokedBetweenTwoCalls_StopsTheSecondCallBeforeAnyProvider()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        await AcceptAsync(ana.Client);

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Chat, Question())).StatusCode);
        Assert.Equal(1, provider.Calls);

        // The other member switches it off for the group; the same session asks again.
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.DeleteAsync($"{Consent}?scope=group")).StatusCode);
        var second = await ana.Client.PostAsJsonAsync(Chat, Question());

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal("AI_CONSENT_REQUIRED", (await ErrorOf(second)).Code);
        Assert.Equal(1, provider.Calls);
        Assert.Single(factory.UsageRows());
    }

    [Fact]
    public async Task TheChat_OfAGroupOfOnePerson_TellsTheModelThereIsNobodyElse_AndRefusesAnAnswerThatInventsAPartner()
    {
        var first = new StubLlmProvider("gemini", "gemini-flash-lite-latest", _ => StubLlmProvider.Answer("Este mês foi bom para {{A}} e {{B}}."));
        var second = new StubLlmProvider("gemini", "gemini-2.5-flash", _ => StubLlmProvider.Answer("Este mês foi bom para {{A}}."));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(first, second));
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        await AcceptAsync(ana.Client);

        var response = await ana.Client.PostAsJsonAsync(Chat, Question());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetString();
        Assert.Equal("Este mês foi bom para Ana.", reply);
        Assert.DoesNotContain("alguém do grupo", reply);

        var prompt = Assert.Single(first.Requests).SystemPrompt;
        Assert.Contains("uma única pessoa", prompt);
        Assert.Contains("{{A}}", prompt);
        Assert.DoesNotContain("{{B}}", prompt);
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public async Task TheAcceptanceOfOneGroup_DoesNotSwitchAnotherGroupOn_AndTheUsageOnlySumsTheGroupOfTheToken()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        var carla = await MemberWithGroupAsync(factory, "Carla Exemplo");

        await AcceptAsync(ana.Client);

        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        var other = await StatusOf(carla.Client);
        Assert.False(other.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, other.GetProperty("acceptedBy").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await carla.Client.PostAsJsonAsync(Chat, Question())).StatusCode);

        // Two calls of Ana's group; then Carla's group switches on and makes one.
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Chat, Question())).StatusCode);
        await AcceptAsync(carla.Client);
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.PostAsJsonAsync(Chat, Question())).StatusCode);

        var ofAna = await UsageOf(ana.Client, "?days=30");
        var ofCarla = await UsageOf(carla.Client, "?days=30");
        Assert.Equal(2, ofAna.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("calls").GetInt32()));
        Assert.Equal(1, ofCarla.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("calls").GetInt32()));
        Assert.Equal(2, Assert.Single(ofAna.GetProperty("byFeature").EnumerateArray()).GetProperty("calls").GetInt32());
        Assert.Equal(2, ofAna.GetProperty("providersToday").EnumerateArray().Sum(p => p.GetProperty("calls").GetInt32()));
        Assert.Equal(1, ofCarla.GetProperty("providersToday").EnumerateArray().Sum(p => p.GetProperty("calls").GetInt32()));
        Assert.Equal(2, ofAna.GetProperty("groupBudget").GetProperty("callsToday").GetInt32());
        Assert.Equal(1, ofCarla.GetProperty("groupBudget").GetProperty("callsToday").GetInt32());
        Assert.Equal(2, (await StatusOf(ana.Client)).GetProperty("budget").GetProperty("callsToday").GetInt32());
    }

    // ---------------------------------------------------------------- consumption

    [Theory]
    [InlineData("?days=0")]
    [InlineData("?days=91")]
    [InlineData("?days=-1")]
    [InlineData("?days=abc")]
    public async Task TheUsage_OutsideOneToNinetyDays_Is400InvalidDays(string query)
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var response = await ana.Client.GetAsync(Usage + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_DAYS", (await ErrorOf(response)).Code);
    }

    [Theory]
    [InlineData("", 30)]
    [InlineData("?days=1", 1)]
    [InlineData("?days=90", 90)]
    public async Task TheUsage_HasOneEntryPerDay_AndCountsFailuresApart(string query, int expectedDays)
    {
        var now = DateTime.UtcNow;
        await using var factory = new ChatWebApplicationFactory(enabled: true, frozenNow: now);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        var coupleId = factory.CoupleIds().Single();
        factory.SeedUsage(3, coupleId, "Ok", now);
        factory.SeedUsage(2, coupleId, "Error", now);

        var usage = await UsageOf(ana.Client, query);

        var days = usage.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(expectedDays, days.Count);
        var today = days[^1];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", today.GetProperty("day").GetString()!);
        Assert.Equal(5, today.GetProperty("calls").GetInt32());
        Assert.Equal(2, today.GetProperty("failures").GetInt32());
        Assert.Equal(500, today.GetProperty("inputTokens").GetInt64());
        Assert.Equal(100, today.GetProperty("outputTokens").GetInt64());
        // The budget counts what spent tokens: the three answered calls, not the two errors.
        Assert.Equal(3, usage.GetProperty("groupBudget").GetProperty("callsToday").GetInt32());
        Assert.Equal(25, usage.GetProperty("groupBudget").GetProperty("callLimit").GetInt32());
        Assert.Equal(360, usage.GetProperty("groupBudget").GetProperty("tokensToday").GetInt64());
        Assert.Equal(60000, usage.GetProperty("groupBudget").GetProperty("tokenLimit").GetInt64());
    }

    [Fact]
    public async Task TheQuotaOfAModel_IsNullWhileItIsNotKnown_AndAPercentageOnceItIsConfigured()
    {
        var now = DateTime.UtcNow;
        var catalog = new StubCatalog(new StubLlmProvider("gemini", "gemini-flash-lite-latest"), new StubLlmProvider("gemini", "gemini-2.5-flash"));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: catalog, frozenNow: now, config: new()
        {
            ["Ai:Limits:0:Provider"] = "gemini",
            ["Ai:Limits:0:Model"] = "gemini-2.5-flash",
            ["Ai:Limits:0:Rpd"] = "40",
        });
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        var coupleId = factory.CoupleIds().Single();
        factory.SeedUsage(4, coupleId, "Ok", now);
        factory.SeedUsage(10, coupleId, "Ok", now, model: "gemini-2.5-flash");
        factory.SeedUsage(1, coupleId, "QuotaExhaustedDay", now, model: "gemini-2.5-flash");

        var providers = (await UsageOf(ana.Client, "?days=7")).GetProperty("providersToday").EnumerateArray().ToList();

        var unknown = Assert.Single(providers, p => p.GetProperty("model").GetString() == "gemini-flash-lite-latest");
        Assert.Equal("gemini", unknown.GetProperty("name").GetString());
        Assert.Equal(4, unknown.GetProperty("calls").GetInt32());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("limit").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("percentUsed").ValueKind);
        Assert.False(unknown.GetProperty("exhaustedToday").GetBoolean());

        var known = Assert.Single(providers, p => p.GetProperty("model").GetString() == "gemini-2.5-flash");
        Assert.Equal(11, known.GetProperty("calls").GetInt32());
        Assert.Equal(40, known.GetProperty("limit").GetInt32());
        Assert.Equal(27, known.GetProperty("percentUsed").GetInt32());
        Assert.True(known.GetProperty("exhaustedToday").GetBoolean());
    }

    // ---------------------------------------------------------------- preferences and the welcome screen

    [Fact]
    public async Task TheWelcomeQuestion_IsPendingUntilAnswered_AndComesBackOnceWhenTheOtherMemberSwitchesTheAiOn()
    {
        var clock = new MovingClock(DateTime.UtcNow);
        await using var factory = new ChatWebApplicationFactory(enabled: true, configureServices: clock.Register);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");

        Assert.True((await StatusOf(ana.Client)).GetProperty("onboardingPending").GetBoolean());
        Assert.True((await StatusOf(bruno.Client)).GetProperty("onboardingPending").GetBoolean());

        // Bruno says "not now": the question does not come back to him (on any device: it is on the server).
        var notNow = await PatchAsync(bruno.Client, new { OnboardingAnswered = true });
        Assert.Equal(HttpStatusCode.OK, notNow.StatusCode);
        Assert.False((await notNow.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("onboardingPending").GetBoolean());
        Assert.False((await StatusOf(bruno.Client)).GetProperty("onboardingPending").GetBoolean());
        Assert.False((await StatusOf(bruno.Client)).GetProperty("enabled").GetBoolean());

        // Ana switches it on later: she is done, and Bruno is told once ("Ana switched it on").
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await AcceptAsync(ana.Client);
        Assert.False((await StatusOf(ana.Client)).GetProperty("onboardingPending").GetBoolean());
        Assert.True((await StatusOf(bruno.Client)).GetProperty("onboardingPending").GetBoolean());

        clock.UtcNow = clock.UtcNow.AddHours(1);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(bruno.Client, new { OnboardingAnswered = true })).StatusCode);
        Assert.False((await StatusOf(bruno.Client)).GetProperty("onboardingPending").GetBoolean());
        Assert.True((await StatusOf(bruno.Client)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task TheWeeklyEmail_WithoutAnEmailProvider_Is503EmailNotConfigured()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        factory.Execute("UPDATE users SET email_verified = 1");

        var response = await PatchAsync(ana.Client, new { WeeklyEmail = true });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("EMAIL_NOT_CONFIGURED", (await ErrorOf(response)).Code);
        var status = await StatusOf(ana.Client);
        Assert.False(status.GetProperty("emailConfigured").GetBoolean());
        Assert.False(status.GetProperty("weeklyEmailEnabled").GetBoolean());
    }

    [Fact]
    public async Task TheWeeklyEmail_WithAnUnconfirmedEmail_Is422EmailNotVerified_AndIsStoredOnceTheEmailIsConfirmed()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true, configureServices: services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(new ConfiguredEmailSender());
        });
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var refused = await PatchAsync(ana.Client, new { WeeklyEmail = true });

        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("EMAIL_NOT_VERIFIED", (await ErrorOf(refused)).Code);
        var before = await StatusOf(ana.Client);
        Assert.True(before.GetProperty("emailConfigured").GetBoolean());
        Assert.False(before.GetProperty("emailVerified").GetBoolean());
        Assert.False(before.GetProperty("weeklyEmailEnabled").GetBoolean());

        factory.Execute("UPDATE users SET email_verified = 1");
        var accepted = await PatchAsync(ana.Client, new { WeeklyEmail = true });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = await StatusOf(ana.Client);
        Assert.True(after.GetProperty("emailVerified").GetBoolean());
        Assert.True(after.GetProperty("weeklyEmailEnabled").GetBoolean());
        // Only what was sent changes: the welcome question is still unanswered.
        Assert.True(after.GetProperty("onboardingPending").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, new { WeeklyEmail = false })).StatusCode);
        Assert.False((await StatusOf(ana.Client)).GetProperty("weeklyEmailEnabled").GetBoolean());
    }

    // ---------------------------------------------------------------- gates of every route

    [Theory]
    [InlineData("GET", Status)]
    [InlineData("POST", Consent)]
    [InlineData("DELETE", Consent + "?scope=mine")]
    [InlineData("PATCH", Preferences)]
    [InlineData("GET", Usage)]
    public async Task EveryRoute_NeedsASession_AndAGroup(string method, string path)
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, method, path)).StatusCode);

        var loner = await RegisterAsync(factory, "Sem Grupo");
        var response = await SendAsync(loner.Client, method, path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("COUPLE_REQUIRED", (await ErrorOf(response)).Code);
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

    private static async Task<JsonElement> UsageOf(HttpClient client, string query)
    {
        var response = await client.GetAsync(Usage + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AcceptAsync(HttpClient client)
        => Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Consent, new { Version = 1 })).StatusCode);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, object body)
        => client.PatchAsync(Preferences, JsonContent.Create(body));

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PATCH") request.Content = JsonContent.Create(new { Version = 1 });
        return client.SendAsync(request);
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

    /// <summary>A person in a group of their own that has NOT switched the AI on.</summary>
    private static async Task<Member> MemberWithGroupAsync(ChatWebApplicationFactory factory, string name)
        => (await OwnerAsync(factory, name)).Member;

    private static async Task<(Member Member, string JoinCode)> OwnerAsync(ChatWebApplicationFactory factory, string name)
    {
        var member = await RegisterAsync(factory, name);
        var created = await member.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        member.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return (member, body.GetProperty("joinCode").GetString()!);
    }

    /// <summary>The owner and a second member of the same group; nobody switched the AI on.</summary>
    private static async Task<(Member Owner, Member Other)> TwoMembersAsync(ChatWebApplicationFactory factory, string ownerName, string otherName)
    {
        var (owner, joinCode) = await OwnerAsync(factory, ownerName);
        var other = await RegisterAsync(factory, otherName);
        var joined = await other.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var body = await joined.Content.ReadFromJsonAsync<JsonElement>();
        other.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return (owner, other);
    }

    private sealed class MovingClock : IDateTimeProvider
    {
        public MovingClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; set; }

        public void Register(IServiceCollection services)
        {
            services.RemoveAll<IDateTimeProvider>();
            services.AddSingleton<IDateTimeProvider>(this);
        }
    }

    private sealed class ConfiguredEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
