using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Issue #37 — the chain (design 2.4) and its quotas (2.5), with scripted providers on a real database. Every call
/// builds the gateway again on the same database, so whatever a test proves about limits, pauses and budgets comes
/// from the rows of ai_usage and not from memory.
/// </summary>
[Trait("Category", "Ai")]
public sealed class LlmGatewayTests : IDisposable
{
    private const string Gemini = "gemini";
    private const string Flash = "gemini-flash-latest";
    private const string Lite = "gemini-flash-lite-latest";

    private readonly LlmGatewayTestKit _kit = new();
    private readonly Guid _couple = Guid.NewGuid();

    public void Dispose() => _kit.Dispose();

    private static LlmResult R(LlmOutcome outcome) => ScriptedProvider.Result(outcome);

    private (ScriptedProvider First, ScriptedProvider Second) TwoLinks(LlmResult? first = null, LlmResult? second = null)
    {
        var one = new ScriptedProvider(Gemini, Flash);
        if (first is not null) one.Then(first);
        var two = new ScriptedProvider(Gemini, Lite);
        if (second is not null) two.Then(second);
        _kit.Chain(AiChains.Assistant, one, two);
        return (one, two);
    }

    // ---------------------------------------------------------------- the chain

    [Fact]
    public async Task TheFirstLinkAnswers_AndTheCallIsRecorded()
    {
        var (first, second) = TwoLinks();

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
        Assert.Equal("Tudo certo por aqui.", result.Value!.Answer);
        Assert.Equal(Gemini, result.Provider);
        Assert.Equal(Flash, result.Model);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);

        var row = Assert.Single(_kit.Rows());
        Assert.Equal((Gemini, Flash, "Ok", LlmFeatures.Chat), (row.Provider, row.Model, row.Outcome, row.Feature));
        Assert.Equal(_couple, row.CoupleId);
        Assert.Equal((100, 20, 15), (row.InputTokens, row.OutputTokens, row.LatencyMs));
        Assert.Equal(new DateOnly(2026, 10, 8), row.DayUtc);
        Assert.Equal(new DateOnly(2026, 10, 8), row.DayBrt);
    }

    [Fact]
    public async Task ARateLimitedFirstLink_InInteractiveMode_FallsToTheSecondLink()
    {
        var (first, second) = TwoLinks(first: R(LlmOutcome.RateLimitedMinute));

        var result = await _kit.AskAsync(_couple, LlmCallMode.Interactive);

        Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
        Assert.Equal(Lite, result.Model);
        Assert.Equal(1, first.Calls); // no retry on the same link
        Assert.Equal(1, second.Calls);
        Assert.Equal(["RateLimitedMinute", "Ok"], _kit.Rows().Select(r => r.Outcome));
    }

    [Fact]
    public async Task WhenTheMinuteWindowOfTheFirstLinkIsFull_InteractiveSkipsIt_AndJobWaitsForIt()
    {
        var (first, second) = TwoLinks();
        _kit.Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Flash, Rpm = 2 }); // 90% of 2 = 1 per minute
        var window = new LlmMinuteWindow();

        Assert.Equal(Flash, (await _kit.AskAsync(_couple, LlmCallMode.Interactive, window: window)).Model);
        _kit.Clock.Advance(TimeSpan.FromSeconds(20));

        // Interactive: the busy link is skipped, the worse model answers now.
        var interactive = await _kit.AskAsync(_couple, LlmCallMode.Interactive, window: window);
        Assert.Equal(Lite, interactive.Model);
        Assert.Equal((1, 1), (first.Calls, second.Calls));
        Assert.Empty(_kit.Waits);

        // Job: waits for the window of the better model (simulated time) instead of falling to the worse one.
        var before = _kit.Clock.UtcNow;
        var job = await _kit.AskAsync(_couple, LlmCallMode.Job, LlmFeatures.InsightWeekly, window);
        Assert.Equal(LlmGatewayOutcome.Ok, job.Outcome);
        Assert.Equal(Flash, job.Model);
        Assert.Equal((2, 1), (first.Calls, second.Calls));
        var waited = Assert.Single(_kit.Waits);
        Assert.InRange(waited, TimeSpan.FromSeconds(39), TimeSpan.FromSeconds(41));
        Assert.Equal(before + waited, _kit.Clock.UtcNow);
    }

    [Fact]
    public async Task AJob_DoesNotWaitMoreThanSixtySeconds_ForAWindow()
    {
        var (first, second) = TwoLinks();
        _kit.Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Flash, Rpm = 2 });
        _kit.Options.JobMaxWait = TimeSpan.FromSeconds(10);
        var window = new LlmMinuteWindow();
        await _kit.AskAsync(_couple, LlmCallMode.Job, LlmFeatures.InsightWeekly, window);

        var job = await _kit.AskAsync(_couple, LlmCallMode.Job, LlmFeatures.InsightWeekly, window);

        Assert.Equal(Lite, job.Model);
        Assert.Empty(_kit.Waits);
        Assert.Equal((1, 1), (first.Calls, second.Calls));
    }

    [Fact]
    public async Task AJob_WhoseLinkAnswersRateLimited_MovesOn_WithoutRetryingTheSameLink()
    {
        var (first, second) = TwoLinks(first: R(LlmOutcome.RateLimitedMinute));

        var job = await _kit.AskAsync(_couple, LlmCallMode.Job, LlmFeatures.InsightWeekly);

        Assert.Equal(Lite, job.Model);
        Assert.Equal((1, 1), (first.Calls, second.Calls));
    }

    [Fact]
    public async Task AnAnswerThatDoesNotFitTheSchema_CountsAsInvalidOutput_AndTheChainMovesOn()
    {
        var first = new ScriptedProvider(Gemini, Flash)
            .Then(ScriptedProvider.OkResult("""{"reply":"sem o campo pedido"}"""))
            .Then(ScriptedProvider.OkResult("isto não é JSON"))
            .Then(ScriptedProvider.OkResult("""{"answer":"ok","refs":"deveria ser lista"}"""))
            .Then(ScriptedProvider.OkResult("""{"answer":"ok","refs":[],"extra":1}"""));
        var second = new ScriptedProvider(Gemini, Lite);
        _kit.Chain(AiChains.Assistant, first, second);

        for (var i = 0; i < 4; i++)
        {
            var result = await _kit.AskAsync(_couple);
            Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
            Assert.Equal(Lite, result.Model);
        }

        var rows = _kit.Rows();
        Assert.Equal(4, rows.Count(r => r.Model == Flash && r.Outcome == "InvalidOutput"));
        Assert.Equal(4, rows.Count(r => r.Model == Lite && r.Outcome == "Ok"));
        // The tokens of the invalid answer were spent and are recorded.
        Assert.All(rows.Where(r => r.Outcome == "InvalidOutput"), r => Assert.Equal(120, r.InputTokens + r.OutputTokens));
    }

    [Fact]
    public async Task WhenEveryLinkFails_TheResultIsAllProvidersFailed()
    {
        var (first, second) = TwoLinks(R(LlmOutcome.Error), R(LlmOutcome.Timeout));

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.AllProvidersFailed, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal((1, 1), (first.Calls, second.Calls));
        Assert.Equal(["Error", "Timeout"], _kit.Rows().Select(r => r.Outcome));
    }

    [Fact]
    public async Task AProviderThatThrows_OrHangs_IsAFailedLink_NotAFailedRequest()
    {
        var throwing = new ScriptedProvider(Gemini, Flash).Then((_, _) => throw new InvalidOperationException("boom"));
        var hanging = new ScriptedProvider(Gemini, Lite).Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ScriptedProvider.OkResult();
        });
        var third = new ScriptedProvider(Gemini, "gemini-2.5-flash");
        _kit.Chain(AiChains.Assistant, throwing, hanging, third);
        _kit.Options.LinkTimeout = TimeSpan.FromMilliseconds(150);

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
        Assert.Equal("gemini-2.5-flash", result.Model);
        Assert.Equal(["Error", "Timeout", "Ok"], _kit.Rows().Select(r => r.Outcome));
    }

    [Fact]
    public async Task AnInteractiveCall_StopsWhenItsTotalTimeIsOver()
    {
        var slow = new ScriptedProvider(Gemini, Flash).Then((_, _) =>
        {
            _kit.Clock.Advance(TimeSpan.FromSeconds(21)); // the first link used the whole 20 s of the route
            return Task.FromResult(R(LlmOutcome.Error));
        });
        var second = new ScriptedProvider(Gemini, Lite);
        _kit.Chain(AiChains.Assistant, slow, second);

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.AllProvidersFailed, result.Outcome);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task ALinkOfAProviderOutsideTheConsentedList_IsIgnoredWithAWarning_AndTheChainGoesOn()
    {
        var groq = new ScriptedProvider("groq", "openai/gpt-oss-120b");
        var gemini = new ScriptedProvider(Gemini, Flash);
        _kit.Chain(AiChains.Assistant, groq, gemini);

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
        Assert.Equal(Gemini, result.Provider);
        Assert.Equal(0, groq.Calls);
        var warning = Assert.Single(_kit.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("groq", warning.Message);
        Assert.DoesNotContain(_kit.Rows(), r => r.Provider == "groq");
        Assert.Equal(["gemini"], AiConsentCoverage.Providers);
    }

    [Fact]
    public async Task ALinkWhoseProviderHasNoKey_IsSkipped()
    {
        var gemini = new ScriptedProvider(Gemini, Lite);
        _kit.Catalog.Add(gemini);
        _kit.Options.Chains[AiChains.Assistant] = [new LlmLink(Gemini, Flash), new LlmLink(Gemini, Lite)]; // no provider for the first

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(Lite, result.Model);
        Assert.Single(_kit.Rows());
    }

    [Fact]
    public async Task WithTheFakeProviderOn_ItIsTheOnlyLinkOfEveryChain_AndIsNotSubjectToTheConsentedList()
    {
        var (first, _) = TwoLinks();
        var fake = new ScriptedProvider(AiOptions.FakeProviderName, AiOptions.FakeProviderName);
        _kit.Catalog.Add(fake);
        _kit.Options.UseFakeProvider = true;

        foreach (var feature in new[] { LlmFeatures.Chat, LlmFeatures.InsightWeekly, LlmFeatures.InsightDaily, LlmFeatures.Categorize, LlmFeatures.Education })
        {
            var result = await _kit.AskAsync(_couple, feature: feature);
            Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
            Assert.Equal(AiOptions.FakeProviderName, result.Provider);
        }

        Assert.Equal(5, fake.Calls);
        Assert.Equal(0, first.Calls);
        Assert.DoesNotContain(_kit.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Disabled_AndNotConsented_NeverReachAProvider()
    {
        var (first, second) = TwoLinks();

        _kit.Options.Disabled = true;
        Assert.Equal(LlmGatewayOutcome.Disabled, (await _kit.AskAsync(_couple)).Outcome);

        _kit.Options.Disabled = false;
        _kit.Consent.Enabled = false;
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await _kit.AskAsync(_couple)).Outcome);

        Assert.Equal((0, 0), (first.Calls, second.Calls));
        Assert.Empty(_kit.Rows());
    }

    [Fact]
    public async Task Consent_IsCheckedAgainRightBeforeEachLink()
    {
        var first = new ScriptedProvider(Gemini, Flash).Then((_, _) =>
        {
            _kit.Consent.Enabled = false; // the group switched the AI off while the first link was answering
            return Task.FromResult(R(LlmOutcome.Error));
        });
        var second = new ScriptedProvider(Gemini, Lite);
        _kit.Chain(AiChains.Assistant, first, second);

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.NotConsented, result.Outcome);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task ARejectedAnswer_TriesTheNextLinkOnce_AndTwoRejectionsEndInOutputRejected()
    {
        var first = new ScriptedProvider(Gemini, Flash).Then(ScriptedProvider.OkResult("""{"answer":"ruim","refs":[]}"""));
        var second = new ScriptedProvider(Gemini, Lite).Then(ScriptedProvider.OkResult("""{"answer":"boa","refs":[]}"""));
        var third = new ScriptedProvider(Gemini, "gemini-2.5-flash");
        _kit.Chain(AiChains.Assistant, first, second, third);

        var accepted = await _kit.Gateway().GenerateAsync<TestAnswer>(
            _couple, LlmGatewayTestKit.Request(), LlmCallMode.Interactive, a => a.Answer != "ruim", CancellationToken.None);
        Assert.Equal(LlmGatewayOutcome.Ok, accepted.Outcome);
        Assert.Equal("boa", accepted.Value!.Answer);
        Assert.Equal(Lite, accepted.Model);

        var rejected = await _kit.Gateway().GenerateAsync<TestAnswer>(
            _couple, LlmGatewayTestKit.Request(), LlmCallMode.Interactive, _ => false, CancellationToken.None);
        Assert.Equal(LlmGatewayOutcome.OutputRejected, rejected.Outcome);
        Assert.Null(rejected.Value);
        Assert.Equal((2, 2, 0), (first.Calls, second.Calls, third.Calls));
    }

    [Fact]
    public async Task AFeatureWithoutAChain_IsAProgrammingError()
        => await Assert.ThrowsAsync<ArgumentException>(() => _kit.AskAsync(_couple, feature: "desconhecida"));

    [Theory]
    [InlineData(LlmFeatures.Categorize, 0)]
    [InlineData(LlmFeatures.Ocr, 0)]
    [InlineData(LlmFeatures.Chat, 0.2)]
    [InlineData(LlmFeatures.InsightDaily, 0.2)]
    [InlineData(LlmFeatures.InsightWeekly, 0.2)]
    [InlineData(LlmFeatures.InsightMonthly, 0.2)]
    [InlineData(LlmFeatures.Education, 0.2)]
    public void Temperature_IsZeroForExtractionAndCategorization_AndPointTwoForTheRest(string feature, double expected)
        => Assert.Equal((decimal)expected, LlmFeatures.TemperatureOf(feature));

    // ---------------------------------------------------------------- 429: the day's quota, pauses

    [Fact]
    public async Task ADailyQuota429_TakesTheLinkOutUntilTheNextUtcDay()
    {
        var (first, second) = TwoLinks(first: R(LlmOutcome.QuotaExhaustedDay));
        first.Then(ScriptedProvider.OkResult());

        Assert.Equal(Lite, (await _kit.AskAsync(_couple)).Model);
        Assert.Equal(1, first.Calls);

        // Hours later, same UTC day, after "a restart": still out.
        _kit.Clock.UtcNow = new DateTime(2026, 10, 8, 23, 59, 0, DateTimeKind.Utc);
        Assert.Equal(Lite, (await _kit.AskAsync(_couple)).Model);
        Assert.Equal(1, first.Calls);

        // The provider's day turned (UTC): the link is tried again.
        _kit.Clock.UtcNow = new DateTime(2026, 10, 9, 0, 0, 1, DateTimeKind.Utc);
        Assert.Equal(Flash, (await _kit.AskAsync(_couple)).Model);
        Assert.Equal(2, first.Calls);
    }

    [Fact]
    public async Task Other429s_PauseTheLinkForTwoThenTenThenThirtyMinutes_AndTheFourthInARowExhaustsTheDay()
    {
        var first = new ScriptedProvider(Gemini, Flash, R(LlmOutcome.RateLimitedMinute));
        var second = new ScriptedProvider(Gemini, Lite);
        _kit.Chain(AiChains.Assistant, first, second);

        async Task AskAsync(int expectedCallsOfTheFirstLink)
        {
            var result = await _kit.AskAsync(_couple);
            Assert.Equal(Lite, result.Model);
            Assert.Equal(expectedCallsOfTheFirstLink, first.Calls);
        }

        await AskAsync(1);                                   // 1st 429 → paused for 2 minutes
        _kit.Clock.Advance(TimeSpan.FromSeconds(119));
        await AskAsync(1);                                   // still paused
        _kit.Clock.Advance(TimeSpan.FromSeconds(2));
        await AskAsync(2);                                   // 2nd 429 → paused for 10 minutes
        _kit.Clock.Advance(TimeSpan.FromMinutes(9));
        await AskAsync(2);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1.1));
        await AskAsync(3);                                   // 3rd 429 → paused for 30 minutes
        _kit.Clock.Advance(TimeSpan.FromMinutes(29));
        await AskAsync(3);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1.1));
        await AskAsync(4);                                   // 4th in a row → out for the day
        _kit.Clock.Advance(TimeSpan.FromHours(6));
        await AskAsync(4);

        var firstLinkRows = _kit.Rows().Where(r => r.Model == Flash).Select(r => r.Outcome);
        Assert.Equal(["RateLimitedMinute", "RateLimitedMinute", "RateLimitedMinute", "QuotaExhaustedDay"], firstLinkRows);
    }

    [Fact]
    public async Task AnOk_ResetsTheCountOf429sInARow()
    {
        var first = new ScriptedProvider(Gemini, Flash,
            R(LlmOutcome.RateLimitedMinute), ScriptedProvider.OkResult(), R(LlmOutcome.RateLimitedMinute), ScriptedProvider.OkResult());
        var second = new ScriptedProvider(Gemini, Lite);
        _kit.Chain(AiChains.Assistant, first, second);

        Assert.Equal(Lite, (await _kit.AskAsync(_couple)).Model);    // 429 → 2 minutes
        _kit.Clock.Advance(TimeSpan.FromMinutes(2.1));
        Assert.Equal(Flash, (await _kit.AskAsync(_couple)).Model);   // Ok: the count is back to zero
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Lite, (await _kit.AskAsync(_couple)).Model);    // 429 again: the FIRST of a new run → 2 minutes, not 10
        _kit.Clock.Advance(TimeSpan.FromMinutes(2.1));
        Assert.Equal(Flash, (await _kit.AskAsync(_couple)).Model);
        Assert.Equal(4, first.Calls);
    }

    [Fact]
    public async Task AnUnknownDailyLimit_BlocksNothing_AndAKnownOneIsUsedUpToNinetyPercent()
    {
        var (first, _) = TwoLinks();

        // Default configuration of the Gemini models: only Rpm; Rpd and Tpd unknown.
        var limit = _kit.Options.LimitFor(Gemini, Flash);
        Assert.Null(limit.Rpd);
        Assert.Null(limit.Tpd);
        Assert.Null(limit.Tpm);
        Assert.Equal(5, limit.Rpm);

        _kit.Seed(400, Gemini, Flash, coupleId: null, feature: LlmFeatures.InsightWeekly, inputTokens: 5000, outputTokens: 1000);
        _kit.Options.JobDailyCalls = 100_000;
        Assert.Equal(Flash, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Model);
        Assert.Equal(1, first.Calls);

        // Rpd = 450 → the chain stops at 405 (90%): 401 rows exist, four more go, the next falls to the reserve.
        _kit.Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Flash, Rpd = 450 });
        for (var i = 0; i < 4; i++)
            Assert.Equal(Flash, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Model);
        Assert.Equal(Lite, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Model);
        Assert.Equal(5, first.Calls);
    }

    [Fact]
    public async Task AKnownDailyTokenLimit_IsAlsoUsedUpToNinetyPercent()
    {
        var (first, _) = TwoLinks();
        _kit.Options.Limits.Add(new AiLimit { Provider = Gemini, Model = Flash, Tpd = 100_000 });
        _kit.Options.JobDailyCalls = 100_000;
        _kit.Seed(1, Gemini, Flash, coupleId: null, feature: LlmFeatures.InsightWeekly, inputTokens: 80_000, outputTokens: 9_500);

        // 89,500 used; this call is estimated at about 1,010 tokens (prompt + the 1,000 of the answer) → over 90,000.
        Assert.Equal(Lite, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Model);
        Assert.Equal(0, first.Calls);
    }

    // ---------------------------------------------------------------- budgets

    [Fact]
    public async Task TheTwentySixthInteractiveCallOfAGroupInABrasiliaDay_IsGroupBudgetExhausted_AndOtherGroupsGoOn()
    {
        var (first, second) = TwoLinks();
        var otherCouple = Guid.NewGuid();
        Assert.Equal(25, _kit.Options.GroupDailyCalls);

        for (var i = 0; i < 25; i++)
            Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(_couple)).Outcome);

        var blocked = await _kit.AskAsync(_couple);
        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, blocked.Outcome);
        Assert.Equal(25, first.Calls + second.Calls);
        Assert.Equal(25, _kit.Rows().Count);

        // Isolation: the other group is not affected; jobs are outside the group's budget.
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(otherCouple)).Outcome);
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(_couple, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Outcome);

        // At midnight of Brasília (03:00 UTC) the group's day starts again.
        _kit.Clock.UtcNow = new DateTime(2026, 10, 9, 2, 59, 0, DateTimeKind.Utc);
        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, (await _kit.AskAsync(_couple)).Outcome);
        _kit.Clock.UtcNow = new DateTime(2026, 10, 9, 3, 0, 1, DateTimeKind.Utc);
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(_couple)).Outcome);
    }

    [Fact]
    public async Task ACallThatCameBackInvalid_AlsoCountsInTheGroupBudget_AndA429DoesNot()
    {
        var (first, _) = TwoLinks();
        _kit.Options.GroupDailyCalls = 3;
        _kit.Seed(2, Gemini, Flash, _couple, outcome: "InvalidOutput");
        _kit.Seed(5, Gemini, "gemini-2.5-flash", _couple, outcome: "RateLimitedMinute", inputTokens: 0, outputTokens: 0);

        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(_couple)).Outcome);
        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, (await _kit.AskAsync(_couple)).Outcome);
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public async Task TheGroupTokenBudget_AlsoStopsTheGroup()
    {
        var (first, _) = TwoLinks();
        Assert.Equal(60_000, _kit.Options.GroupDailyTokens);
        _kit.Seed(1, Gemini, Flash, _couple, inputTokens: 50_000, outputTokens: 9_999);
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(_couple)).Outcome); // +120 tokens → 60,119

        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, (await _kit.AskAsync(_couple)).Outcome);
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public async Task TheGlobalInteractiveCeiling_SumsEveryGroup_SurvivesARestart_AndLeavesJobsAlone()
    {
        var (first, second) = TwoLinks();
        Assert.Equal(150, _kit.Options.GlobalDailyInteractiveCalls);
        _kit.Options.GlobalDailyInteractiveCalls = 3;

        for (var i = 0; i < 3; i++)
            Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(Guid.NewGuid())).Outcome);

        // 4th interactive call of the Brasília day, of yet another group; AskAsync builds a new gateway (a restart).
        var blocked = await _kit.AskAsync(Guid.NewGuid());
        Assert.Equal(LlmGatewayOutcome.GlobalBudgetExhausted, blocked.Outcome);
        Assert.Null(blocked.Value);
        Assert.Equal(3, first.Calls + second.Calls);

        // A job goes on as usual.
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(Guid.NewGuid(), LlmCallMode.Job, LlmFeatures.InsightWeekly)).Outcome);
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightMonthly)).Outcome);
        Assert.Equal(LlmGatewayOutcome.GlobalBudgetExhausted, (await _kit.AskAsync(Guid.NewGuid())).Outcome);

        // Until midnight of Brasília.
        _kit.Clock.UtcNow = new DateTime(2026, 10, 9, 3, 0, 1, DateTimeKind.Utc);
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task Jobs_HaveTheirOwnDailyCeiling()
    {
        var (first, _) = TwoLinks();
        Assert.Equal(60, _kit.Options.JobDailyCalls);
        _kit.Options.JobDailyCalls = 2;

        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Outcome);
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightMonthly)).Outcome);
        Assert.Equal(LlmGatewayOutcome.GlobalBudgetExhausted, (await _kit.AskAsync(null, LlmCallMode.Job, LlmFeatures.InsightWeekly)).Outcome);
        Assert.Equal(2, first.Calls);

        // The Assistant is not affected by the jobs' ceiling.
        Assert.Equal(LlmGatewayOutcome.Ok, (await _kit.AskAsync(_couple)).Outcome);
    }

    // ---------------------------------------------------------------- what ai_usage holds

    [Fact]
    public async Task AiUsage_HoldsOnlyMetadata_NeverThePromptNorTheAnswer()
    {
        const string secretQuestion = "PERGUNTA-QUE-NAO-PODE-SER-GRAVADA";
        const string secretAnswer = "RESPOSTA-QUE-NAO-PODE-SER-GRAVADA";
        var provider = new ScriptedProvider(Gemini, Flash, ScriptedProvider.OkResult($$"""{"answer":"{{secretAnswer}}","refs":[]}"""));
        _kit.Chain(AiChains.Assistant, provider);

        var result = await _kit.Gateway().GenerateAsync<TestAnswer>(
            _couple, LlmGatewayTestKit.Request(question: secretQuestion), LlmCallMode.Interactive, CancellationToken.None);
        Assert.Equal(secretAnswer, result.Value!.Answer);

        using var db = _kit.NewContext();
        var entity = db.Model.FindEntityType(typeof(CoupleSync.Domain.Entities.AiUsage))!;
        Assert.Equal("ai_usage", entity.GetTableName());
        Assert.Equal(
            new[]
            {
                "couple_id", "created_at_utc", "day_brt", "day_utc", "feature", "id", "input_tokens", "latency_ms", "model",
                "outcome", "output_tokens", "provider",
            },
            entity.GetProperties().Select(p => p.GetColumnName()).OrderBy(c => c, StringComparer.Ordinal));

        // Every value of every column of the stored row, as text.
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ai_usage";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(12, reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            Assert.DoesNotContain("PODE-SER-GRAVADA", value);
            Assert.True(value.Length <= 80, $"column {reader.GetName(i)} holds a long text");
        }

        Assert.DoesNotContain(_kit.Log.Entries, e => e.Message.Contains(secretQuestion) || e.Message.Contains(secretAnswer));
    }

    [Fact]
    public async Task AiUsage_HasNoGlobalGroupFilter_SoEveryGroupReadNamesTheGroup()
    {
        var otherCouple = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 8);
        _kit.Seed(3, Gemini, Flash, _couple);
        _kit.Seed(4, Gemini, Flash, otherCouple, inputTokens: 1000, outputTokens: 0);
        _kit.Seed(2, Gemini, Flash, coupleId: null);

        // The table is accounting (it has rows without a group): it is not ICoupleScoped...
        Assert.False(typeof(CoupleSync.Domain.Interfaces.ICoupleScoped).IsAssignableFrom(typeof(CoupleSync.Domain.Entities.AiUsage)));
        // ...so a context of a request of one group still sees every row,
        await using var requestContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_kit.NewContext().Database.GetDbConnection()).Options,
            new FixedCoupleContext(_couple));
        Assert.Equal(9, await requestContext.AiUsages.CountAsync());

        // ...and the repository is what separates the groups, even inside that request.
        var repository = new AiUsageRepository(requestContext);
        var mine = await repository.GetGroupDayAsync(_couple, day, LlmFeatures.Interactive, CancellationToken.None);
        var theirs = await repository.GetGroupDayAsync(otherCouple, day, LlmFeatures.Interactive, CancellationToken.None);
        Assert.Equal((3, 45L), (mine.Calls, mine.Tokens));
        Assert.Equal((4, 4000L), (theirs.Calls, theirs.Tokens));
        Assert.Equal(0, (await repository.GetGroupDayAsync(Guid.NewGuid(), day, LlmFeatures.Interactive, CancellationToken.None)).Calls);
        Assert.Equal(9, await repository.CountDayAsync(day, LlmFeatures.Interactive, CancellationToken.None));
    }

    private sealed class FixedCoupleContext : ICoupleContext
    {
        public FixedCoupleContext(Guid coupleId) => CoupleId = coupleId;

        public Guid? CoupleId { get; }
    }
}
