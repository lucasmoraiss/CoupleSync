using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleSync.IntegrationTests.AiChat;

/// <summary>
/// The AI of a group through HTTP, on SQLite, with stub providers; the rows are read with raw SQL. The AI is on for
/// every group, with no question and no acceptance: only "off for the group", asked on purpose by a member, switches
/// it off, and activating switches it back on. The acceptances of before are still stored and listed as they are —
/// they switch nothing on or off — and the routes the installed app calls keep answering in the same format.
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
    public async Task AGroupIsEnabledWithNoAcceptance_AndWhoAcceptsIsListedWithTheNameAndTheDate_ForBothMembers()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");

        // Nobody accepted anything: the group is on, and no acceptance is invented for anyone.
        var before = await StatusOf(bruno.Client);
        Assert.True(before.GetProperty("available").GetBoolean());
        Assert.True(before.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, before.GetProperty("acceptedBy").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("myAcceptance").ValueKind);
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));

        var askedAt = DateTime.UtcNow.AddSeconds(-5);
        var accepted = await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.Equal(2, status.GetProperty("consentVersion").GetInt32());
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

        // Exactly the fields the installed app reads, none removed or renamed, in the order of always.
        Assert.Equal(
            [
                "available", "enabled", "consentVersion", "acceptedBy", "myAcceptance", "onboardingPending", "weeklyEmailEnabled",
                "emailVerified", "emailConfigured", "providers", "features", "budget",
            ],
            status.EnumerateObject().Select(p => p.Name).ToArray());

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

    /// <summary>
    /// What the JavaScript already installed decides from the status (mobile/src/modules/ai/aiStatus.ts as published):
    /// <c>shouldOpenWelcome</c> = available AND onboardingPending; <c>shouldShowActivationCard</c> = available AND NOT
    /// enabled; <c>assistantGate</c> = "ready" when available AND enabled; <c>aiUploadConsent</c> = available AND
    /// enabled; <c>parseStoredAiStatus</c> needs the booleans, the two lists, the two objects and myAcceptance null or
    /// an object. With these exact values it means "on, no question" in every state a group can be in.
    /// </summary>
    [Theory]
    [InlineData("new group")]
    [InlineData("answered not now")]
    [InlineData("acceptance of version 1")]
    [InlineData("acceptance of version 2")]
    [InlineData("acceptances revoked before")]
    public async Task ForTheInstalledApp_TheStatusOfAnyGroupMeansOnWithoutAQuestion_AndTheChatAnswers(string state)
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        var group = Assert.Single(factory.CoupleIds());
        var expectedAcceptances = 0;
        switch (state)
        {
            case "answered not now":
                Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, new { OnboardingAnswered = true })).StatusCode);
                break;
            case "acceptance of version 1":
                factory.AddConsent(group, ana.UserId, version: 1, revoked: false);
                break;
            case "acceptance of version 2":
                factory.AddConsent(group, ana.UserId, version: 2, revoked: false);
                expectedAcceptances = 1;
                break;
            case "acceptances revoked before":
                // As production has them: rows revoked by the old "off for the group" / "take my acceptance back".
                factory.AddConsent(group, ana.UserId, version: 1, revoked: true);
                factory.AddConsent(group, ana.UserId, version: 2, revoked: true);
                factory.AddConsent(group, bruno.UserId, version: 2, revoked: true);
                break;
        }

        var rowsBefore = factory.Scalar<long>("SELECT count(*) FROM ai_consents");

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.True(status.GetProperty("available").GetBoolean());
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());
            Assert.Equal(2, status.GetProperty("consentVersion").GetInt32());
            Assert.Equal(JsonValueKind.Array, status.GetProperty("acceptedBy").ValueKind);
            Assert.Equal(expectedAcceptances, status.GetProperty("acceptedBy").GetArrayLength());
            Assert.Equal(JsonValueKind.Array, status.GetProperty("providers").ValueKind);
            Assert.Equal(JsonValueKind.Object, status.GetProperty("features").ValueKind);
            Assert.True(status.GetProperty("features").GetProperty("assistant").GetBoolean());
            Assert.Equal(JsonValueKind.Object, status.GetProperty("budget").ValueKind);
            // Nobody is given an acceptance they did not give.
            var mine = status.GetProperty("myAcceptance").ValueKind;
            Assert.Equal(member == ana && expectedAcceptances == 1 ? JsonValueKind.Object : JsonValueKind.Null, mine);

            var chat = await member.Client.PostAsJsonAsync(Chat, Question());
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        }

        Assert.Equal(2, provider.Calls);
        // Reading the status and asking stored and erased no acceptance.
        Assert.Equal(rowsBefore, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
    }

    /// <summary>The acceptance of a deactivated account is not listed; the group is on all the same.</summary>
    [Fact]
    public async Task TheAcceptanceOfADeactivatedAccount_IsNotListed_AndTheGroupStaysOn()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.True((await StatusOf(bruno.Client)).GetProperty("enabled").GetBoolean());

        factory.Execute($"UPDATE users SET is_active = 0 WHERE upper(id) = '{ana.UserId.ToString().ToUpperInvariant()}'");

        var status = await StatusOf(bruno.Client);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, status.GetProperty("acceptedBy").GetArrayLength());
        var chat = await bruno.Client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Quanto gastamos?", History = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
    }

    /// <summary>
    /// No version is refused any more (the 409 AI_CONSENT_VERSION_OUTDATED is gone): activating always answers 200
    /// with the status and switches the group on. An acceptance is only written for the text in force — nobody is
    /// recorded as having accepted a text of another version.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(99, 0)]
    public async Task ActivatingWithAnyVersion_Is200_SwitchesTheGroupOn_AndOnlyTheVersionInForceIsStoredAsAnAcceptance(int version, int expectedRows)
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        // Off on purpose first, so that "switches the group on" is something this request did.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.DeleteAsync($"{Consent}?scope=group")).StatusCode);
        Assert.False((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());

        var response = await ana.Client.PostAsJsonAsync(Consent, new { Version = version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answered = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(answered.GetProperty("enabled").GetBoolean());
        Assert.False(answered.GetProperty("onboardingPending").GetBoolean());
        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        Assert.Equal(expectedRows, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(expectedRows, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE version = 2 AND revoked_at_utc IS NULL"));
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

        var response = await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 });
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
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("onboardingPending").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
    }

    // ---------------------------------------------------------------- switching off

    [Fact]
    public async Task ScopeGroup_InAGroupWithNoAcceptance_SwitchesItOffForEveryone_UntilSomeoneActivatesAgain()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Chat, Question())).StatusCode);
        Assert.Equal(1, provider.Calls);

        // "Desligar para o grupo" of the installed app, by a member who never accepted anything.
        var off = await bruno.Client.DeleteAsync($"{Consent}?scope=group");

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.True(status.GetProperty("available").GetBoolean());
            Assert.False(status.GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("onboardingPending").GetBoolean());

            // The 403 the installed app already knows; here it is true: the group switched it off.
            var chat = await member.Client.PostAsJsonAsync(Chat, Question());
            Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
            var error = await ErrorOf(chat);
            Assert.Equal("AI_CONSENT_REQUIRED", error.Code);
            Assert.Equal("A IA está desligada para este grupo. Para voltar a usar, ligue em Configurações > Inteligência artificial.", error.Message);
        }

        Assert.Equal(1, provider.Calls);
        Assert.Single(factory.UsageRows());

        // "Ativar" of the installed app (version 2), by the other member: on again for both.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);
        Assert.True((await StatusOf(bruno.Client)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.PostAsJsonAsync(Chat, Question())).StatusCode);
        Assert.Equal(2, provider.Calls);
    }

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
    public async Task ScopeMine_RevokesOnlyTheOwnAcceptance_AndNeverSwitchesTheGroupOff()
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

        // The last acceptance taken back, and then "mine" by who has none: 200, and the group is still on.
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.DeleteAsync($"{Consent}?scope=mine")).StatusCode);
        var again = await ana.Client.DeleteAsync($"{Consent}?scope=mine");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());

        foreach (var member in new[] { ana, bruno })
        {
            var status = await StatusOf(member.Client);
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.Equal(0, status.GetProperty("acceptedBy").GetArrayLength());
        }

        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PostAsJsonAsync(Chat, Question())).StatusCode);
        // Nothing erased: the two rows are there, revoked.
        Assert.Equal(2, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NOT NULL"));
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
    public async Task TheMemberWhoLeavesOrIsRemoved_IsNoLongerListed_GetsRevokedAt_LosesTheirPreferences_AndTheGroupStaysOn(bool removedByTheOwner)
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
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, status.GetProperty("acceptedBy").GetArrayLength());
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_consents WHERE revoked_at_utc IS NOT NULL"));
        Assert.Equal(0, factory.Scalar<long>($"SELECT count(*) FROM ai_user_preferences WHERE upper(user_id) = '{bruno.UserId.ToString().ToUpperInvariant()}'"));
        Assert.Equal(1, factory.Scalar<long>($"SELECT count(*) FROM ai_user_preferences WHERE upper(user_id) = '{ana.UserId.ToString().ToUpperInvariant()}'"));

        // And the chat of who stayed is answered: the acceptance that left was not what kept the AI on.
        var chat = await ana.Client.PostAsJsonAsync(Chat, Question());
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Single(factory.UsageRows());
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
    public async Task TheChat_InAGroupThatNeverAccepted_IsAnswered_NotRefusedWithAiConsentRequired()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var response = await ana.Client.PostAsJsonAsync(Chat, Question());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, provider.Calls);
        Assert.Single(factory.UsageRows());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_consents"));
    }

    /// <summary>The limits are the same as before for a group that never accepted: the 25th call of the day is the last.</summary>
    [Fact]
    public async Task TheGroupBudget_StillStopsAGroupThatNeverAccepted_With429()
    {
        var now = DateTime.UtcNow;
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider), frozenNow: now);
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        factory.SeedUsage(25, Assert.Single(factory.CoupleIds()), "Ok", now);

        var response = await ana.Client.PostAsJsonAsync(Chat, Question());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("AI_DAILY_BUDGET_EXHAUSTED", (await ErrorOf(response)).Code);
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>The emergency switch with a group that never accepted: not available, nothing sent — and the group is not "off".</summary>
    [Fact]
    public async Task WithAiDisabled_AGroupThatNeverAccepted_IsNotAvailable_AndNothingIsSent()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: false, catalog: new StubCatalog(provider));
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");

        var status = await StatusOf(ana.Client);
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("onboardingPending").GetBoolean());
        Assert.False(status.GetProperty("features").GetProperty("assistant").GetBoolean());

        var response = await ana.Client.PostAsJsonAsync(Chat, Question());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("AI_CHAT_DISABLED", (await ErrorOf(response)).Code);
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
    public async Task SwitchingItOffForTheGroupBetweenTwoCalls_StopsTheSecondCallBeforeAnyProvider()
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
    public async Task SwitchingOneGroupOffOrOn_DoesNotTouchAnotherGroup_AndTheUsageOnlySumsTheGroupOfTheToken()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-lite-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        var ana = await MemberWithGroupAsync(factory, "Ana Exemplo");
        var carla = await MemberWithGroupAsync(factory, "Carla Exemplo");

        // Carla's group switches itself off: Ana's is still on, and Ana's acceptance is not listed in Carla's.
        Assert.Equal(HttpStatusCode.OK, (await carla.Client.DeleteAsync($"{Consent}?scope=group")).StatusCode);
        await AcceptAsync(ana.Client);

        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        var other = await StatusOf(carla.Client);
        Assert.False(other.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, other.GetProperty("acceptedBy").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await carla.Client.PostAsJsonAsync(Chat, Question())).StatusCode);

        // Ana's group switches itself off and on again: Carla's stays off the whole time.
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.DeleteAsync($"{Consent}?scope=group")).StatusCode);
        await AcceptAsync(ana.Client);
        Assert.False((await StatusOf(carla.Client)).GetProperty("enabled").GetBoolean());

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
    public async Task TheWelcomeQuestion_IsNeverPending_ForAnyone_AndAnsweringItStillWorksAndSwitchesNothingOff()
    {
        var clock = new MovingClock(DateTime.UtcNow);
        await using var factory = new ChatWebApplicationFactory(enabled: true, configureServices: clock.Register);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");

        async Task NobodyIsAskedAsync()
        {
            Assert.False((await StatusOf(ana.Client)).GetProperty("onboardingPending").GetBoolean());
            Assert.False((await StatusOf(bruno.Client)).GetProperty("onboardingPending").GetBoolean());
        }

        // Nobody answered anything yet.
        await NobodyIsAskedAsync();

        // "Agora não" of an installed app that still shows the old screen: 200 in the format of always, stored, and
        // the group is still on.
        var notNow = await PatchAsync(bruno.Client, new { OnboardingAnswered = true });
        Assert.Equal(HttpStatusCode.OK, notNow.StatusCode);
        var answered = await notNow.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(answered.GetProperty("onboardingPending").GetBoolean());
        Assert.True(answered.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_user_preferences WHERE onboarding_answered_at_utc IS NOT NULL"));
        Assert.True((await StatusOf(ana.Client)).GetProperty("enabled").GetBoolean());
        await NobodyIsAskedAsync();

        // Ana accepts after Bruno's answer (what used to bring the question back to him).
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await AcceptAsync(ana.Client);
        await NobodyIsAskedAsync();

        // Off for the group, and on again.
        clock.UtcNow = clock.UtcNow.AddHours(1);
        Assert.Equal(HttpStatusCode.OK, (await bruno.Client.DeleteAsync($"{Consent}?scope=group")).StatusCode);
        await NobodyIsAskedAsync();
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await AcceptAsync(ana.Client);
        await NobodyIsAskedAsync();
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
        // Only what was sent changes: the answer to the welcome question was not stored along.
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM ai_user_preferences WHERE onboarding_answered_at_utc IS NULL"));
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM ai_user_preferences WHERE onboarding_answered_at_utc IS NOT NULL"));

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
        => Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Consent, new { Version = 2 })).StatusCode);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, object body)
        => client.PatchAsync(Preferences, JsonContent.Create(body));

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PATCH") request.Content = JsonContent.Create(new { Version = 2 });
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

    /// <summary>A person in a group of their own; nobody accepted anything.</summary>
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

    /// <summary>The owner and a second member of the same group; nobody accepted anything.</summary>
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
