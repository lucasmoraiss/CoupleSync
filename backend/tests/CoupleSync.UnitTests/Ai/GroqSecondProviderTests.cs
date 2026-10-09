using System.Net;
using System.Text.Json;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Integrations.Llm;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Groq as the second provider (design 2.2, decision 15). Its address, its links and its limits are defaults in code:
/// the only thing production adds is the secret GROQ_API_KEY. Without that key nothing changes — the links of Groq
/// resolve to no provider and the chain is the Gemini one. With it, Groq is the reserve after the Gemini links of
/// every chain. No test talks to a real provider: keys and answers are invented.
/// </summary>
[Trait("Category", "Ai")]
public sealed class GroqSecondProviderTests : IDisposable
{
    private const string FakeKey = "fake-key-not-real-0a1b2c";
    private const string Gemini = "gemini";
    private const string Groq = "groq";
    private const string GroqModel = "openai/gpt-oss-120b";

    private static readonly string[] Fast = ["gemini|gemini-flash-lite-latest", "gemini|gemini-2.5-flash", "gemini|gemini-3.1-flash-lite"];
    private static readonly string[] Quality = ["gemini|gemini-flash-latest", "gemini|gemini-3-flash-preview", "gemini|gemini-flash-lite-latest"];

    private readonly LlmGatewayTestKit _kit = new();
    private readonly Guid _couple = Guid.NewGuid();

    public void Dispose() => _kit.Dispose();

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static LlmProviderCatalog Catalog(IConfiguration config, FakeLlmServer? server = null, string? expectedClient = null)
    {
        var ai = new AiOptions();
        AiConfiguration.Apply(ai, config);
        var providers = new LlmProvidersOptions();
        AiConfiguration.Apply(providers, config, new GeminiOptions());
        server ??= new FakeLlmServer(_ => throw new InvalidOperationException("no call expected"));
        return new LlmProviderCatalog(new FakeLlmServer.Factory(server, expectedClient), Options.Create(ai), Options.Create(providers));
    }

    private static string[] Links(AiOptions options, string chain) => options.Chains[chain].Select(l => $"{l.Provider}|{l.Model}").ToArray();

    // ---------------------------------------------------------------- defaults in code

    [Fact]
    public void EveryChain_KeepsItsGeminiLinksInTheSameOrder_AndEndsWithTheGroqReserve()
    {
        var options = new AiOptions();
        AiConfiguration.Apply(options, Config());

        foreach (var chain in new[] { AiChains.Assistant, AiChains.Categorize, AiChains.Daily })
            Assert.Equal([.. Fast, $"{Groq}|{GroqModel}"], Links(options, chain));
        foreach (var chain in new[] { AiChains.Weekly, AiChains.Education })
            Assert.Equal([.. Quality, $"{Groq}|{GroqModel}"], Links(options, chain));
    }

    [Fact]
    public void WithoutTheGroqKey_TheLinksThatResolve_AreExactlyTheGeminiOnesOfToday()
    {
        var options = new AiOptions();
        var catalog = Catalog(Config(("GEMINI_API_KEY", FakeKey)));

        string[] Resolved(string chain) => options.Chains[chain]
            .Where(l => catalog.Find(l.Provider, l.Model) is not null)
            .Select(l => $"{l.Provider}|{l.Model}")
            .ToArray();

        Assert.Equal(Fast, Resolved(AiChains.Assistant));
        Assert.Equal(Fast, Resolved(AiChains.Categorize));
        Assert.Equal(Fast, Resolved(AiChains.Daily));
        Assert.Equal(Quality, Resolved(AiChains.Weekly));
        Assert.Equal(Quality, Resolved(AiChains.Education));
        Assert.Null(catalog.Find(Groq, GroqModel));

        // A blank key is no key. And the Groq key alone does not make Gemini exist.
        Assert.Null(Catalog(Config(("GEMINI_API_KEY", FakeKey), ("GROQ_API_KEY", "  "))).Find(Groq, GroqModel));
        Assert.Null(Catalog(Config(("GROQ_API_KEY", FakeKey))).Find(Gemini, "gemini-flash-latest"));
    }

    [Fact]
    public async Task WithOnlyTheGroqKey_TheBuiltInEntryCallsGroqsOpenAiEndpoint_WithThatKey()
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.OK, """
            {
              "choices": [ { "index": 0, "message": { "role": "assistant", "content": "{\"answer\":\"Oi\",\"refs\":[]}" }, "finish_reason": "stop" } ],
              "usage": { "prompt_tokens": 210, "completion_tokens": 33, "total_tokens": 243 }
            }
            """));
        var catalog = Catalog(Config(("GROQ_API_KEY", $" {FakeKey} ")), server, OpenAiCompatibleLlmProvider.HttpClientNameOf(Groq));

        Assert.True(catalog.AnyAvailable);
        var provider = Assert.IsType<OpenAiCompatibleLlmProvider>(catalog.Find(Groq, GroqModel));
        var result = await provider.GenerateAsync(LlmGatewayTestKit.Request(), CancellationToken.None);

        Assert.Equal((Groq, GroqModel), (provider.Provider, provider.Model));
        Assert.Equal(LlmOutcome.Ok, result.Outcome);
        var sent = Assert.Single(server.Requests);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", sent.Url);
        Assert.Equal($"Bearer {FakeKey}", sent.Headers["Authorization"]);
        Assert.Equal(GroqModel, JsonDocument.Parse(sent.Body).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void AnEntryNamedGroqInTheConfiguration_ReplacesTheBuiltInOne()
    {
        var entries = AiConfiguration.CompatibleProviders(Config(
            ("Ai:OpenAiCompatible:0:Name", "GROQ"),
            ("Ai:OpenAiCompatible:0:BaseUrl", "https://compat.test/openai/v1"),
            ("Ai:OpenAiCompatible:0:ApiKeyVariable", "OTHER_KEY")));

        var entry = Assert.Single(entries);
        Assert.Equal(("GROQ", "https://compat.test/openai/v1", "OTHER_KEY"), (entry.Name, entry.BaseUrl, entry.ApiKeyVariable));

        var builtIn = Assert.Single(AiConfiguration.CompatibleProviders(Config()));
        Assert.Equal((Groq, "https://api.groq.com/openai/v1", "GROQ_API_KEY"), (builtIn.Name, builtIn.BaseUrl, builtIn.ApiKeyVariable));
    }

    [Fact]
    public void TheLimitsOfGroq_AreTheOnesItPublishes_PerModel_AndCanBeReplaced()
    {
        var options = new AiOptions();
        var limit = options.LimitFor(Groq, GroqModel);
        Assert.Equal(((int?)1000, (long?)200_000L, (int?)30, (long?)8000L), (limit.Rpd, limit.Tpd, limit.Rpm, limit.Tpm));

        AiConfiguration.Apply(options, Config(("Ai:Limits:0:Provider", Groq), ("Ai:Limits:0:Model", GroqModel), ("Ai:Limits:0:Rpd", "50")));
        var replaced = options.LimitFor(Groq, GroqModel);
        Assert.Equal(((int?)50, (long?)null), (replaced.Rpd, replaced.Tpd));

        // Another provider nobody knows: still no limit at all.
        Assert.Null(options.LimitFor("outro", "modelo").Rpm);
    }

    // ---------------------------------------------------------------- the fake provider guard

    [Theory]
    [InlineData("GROQ_API_KEY")]
    public void TheFakeProvider_RefusesToStart_WhenTheGroqKeyHasAValue_EvenWithNothingElseConfigured(string variable)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "true"), (variable, FakeKey))));

        Assert.Contains("Ai__UseFakeProvider", ex.Message);
        Assert.Contains("GROQ_API_KEY", ex.Message);
        Assert.DoesNotContain(FakeKey, ex.Message);

        // Without a value it starts, as it does today.
        FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "true"), (variable, "  ")));
        FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "true")));
    }

    // ---------------------------------------------------------------- the chain

    /// <summary>The default chain of the Assistant with a scripted provider in place of each link.</summary>
    private (ScriptedProvider[] Gemini, ScriptedProvider Groq) DefaultAssistantChain(LlmResult? geminiAnswer)
    {
        var links = _kit.Options.Chains[AiChains.Assistant];
        var gemini = links.Where(l => l.Provider == Gemini)
            .Select(l => geminiAnswer is null ? new ScriptedProvider(l.Provider, l.Model) : new ScriptedProvider(l.Provider, l.Model, geminiAnswer))
            .ToArray();
        var groq = new ScriptedProvider(Groq, GroqModel);
        foreach (var provider in gemini) _kit.Catalog.Add(provider);
        _kit.Catalog.Add(groq);
        return (gemini, groq);
    }

    [Fact]
    public async Task WhenEveryGeminiLinkFails_TheGroqReserveAnswers_AndTheUsageRowSaysGroq()
    {
        var (gemini, groq) = DefaultAssistantChain(ScriptedProvider.Result(LlmOutcome.Error));

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
        Assert.Equal((Groq, GroqModel), (result.Provider, result.Model));
        Assert.All(gemini, provider => Assert.Equal(1, provider.Calls));
        Assert.Equal(3, gemini.Length);
        Assert.Equal(1, groq.Calls);
        // The same request every link received: what the privacy filter let through, nothing else.
        Assert.Same(gemini[0].Requests[0], groq.Requests[0]);

        var rows = _kit.Rows();
        Assert.Equal(4, rows.Count);
        var row = rows[^1];
        Assert.Equal((Groq, GroqModel, "Ok", LlmFeatures.Chat), (row.Provider, row.Model, row.Outcome, row.Feature));
        Assert.Equal(_couple, row.CoupleId);
        Assert.DoesNotContain(_kit.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task WhileGeminiAnswers_GroqIsNeverCalled()
    {
        var (_, groq) = DefaultAssistantChain(geminiAnswer: null);

        var result = await _kit.AskAsync(_couple);

        Assert.Equal((LlmGatewayOutcome.Ok, Gemini), (result.Outcome, result.Provider));
        Assert.Equal(0, groq.Calls);
        Assert.DoesNotContain(_kit.Rows(), r => r.Provider == Groq);
    }

    [Fact]
    public async Task A429OfGroq_PausesOnlyTheGroqLink()
    {
        var gemini = new ScriptedProvider(Gemini, "gemini-flash-latest", ScriptedProvider.Result(LlmOutcome.Error));
        var groq = new ScriptedProvider(Groq, GroqModel, ScriptedProvider.Result(LlmOutcome.RateLimitedMinute));
        _kit.Chain(AiChains.Assistant, gemini, groq);

        Assert.Equal(LlmGatewayOutcome.AllProvidersFailed, (await _kit.AskAsync(_couple)).Outcome);
        Assert.Equal((1, 1), (gemini.Calls, groq.Calls));

        // One minute later: Gemini is tried again, Groq is in its 2-minute pause.
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LlmGatewayOutcome.AllProvidersFailed, (await _kit.AskAsync(_couple)).Outcome);
        Assert.Equal((2, 1), (gemini.Calls, groq.Calls));

        // After the pause it is called again.
        _kit.Clock.Advance(TimeSpan.FromMinutes(2));
        await _kit.AskAsync(_couple);
        Assert.Equal((3, 2), (gemini.Calls, groq.Calls));
        Assert.Equal(2, _kit.Rows().Count(r => r.Provider == Groq && r.Outcome == "RateLimitedMinute"));
    }

    [Fact]
    public async Task TheBudgetOfTheGroup_CountsTheCallsOfBothProvidersTogether()
    {
        _kit.Options.GroupDailyCalls = 4;
        var gemini = new ScriptedProvider(Gemini, "gemini-flash-latest");
        var groq = new ScriptedProvider(Groq, GroqModel);
        _kit.Chain(AiChains.Assistant, gemini, groq);
        _kit.Seed(2, Gemini, "gemini-flash-latest", _couple);
        _kit.Seed(2, Groq, GroqModel, _couple);

        var result = await _kit.AskAsync(_couple);

        Assert.Equal(LlmGatewayOutcome.GroupBudgetExhausted, result.Outcome);
        Assert.Equal((0, 0), (gemini.Calls, groq.Calls));
    }

    [Fact]
    public void TheConsentInForce_CoversGeminiAndGroq_AndNothingElse()
    {
        Assert.Equal(2, AiConsent.CurrentVersion);
        Assert.Equal([Gemini, Groq], AiConsentCoverage.Providers);
        Assert.False(AiConsentCoverage.Covers("mistral"));
    }
}

/// <summary>
/// A group whose acceptance is of the PREVIOUS version of the AI text (the one that named only Google): until someone
/// accepts the version in force, nothing is sent to any provider — neither to the old one nor to the new one.
/// The gateway, the gate and the repository are the real ones, on a real database (SQLite).
/// </summary>
[Trait("Category", "AiChat")]
public sealed class OldAiConsentVersionTests : IDisposable
{
    private readonly LlmGatewayTestKit _kit = new();
    private readonly Guid _coupleId;
    private readonly Guid _anaId;

    public OldAiConsentVersionTests()
    {
        var now = _kit.Clock.UtcNow;
        using var db = _kit.NewContext();
        var ana = User.Create(EmailAddress.From("ana@example.com"), "Ana Exemplo", "hash", now);
        var couple = Couple.Create("ABCD1234", now);
        couple.AddMember(ana, now);
        db.AddRange(ana, couple);
        db.SaveChanges();
        (_coupleId, _anaId) = (couple.Id, ana.Id);
    }

    public void Dispose() => _kit.Dispose();

    private LlmGateway Gateway() => new(
        _kit.Catalog,
        new AiUsageRepository(_kit.NewContext()),
        new ServerAiConsentGate(new AiActivationRepository(_kit.NewContext())),
        Options.Create(_kit.Options),
        new LlmMinuteWindow(),
        _kit.Clock,
        new AdvancingWaiter(_kit.Clock),
        _kit.Log);

    private void Accept(int version)
    {
        using var db = _kit.NewContext();
        db.AiConsents.Add(AiConsent.Accept(_coupleId, _anaId, version, _kit.Clock.UtcNow));
        db.SaveChanges();
    }

    [Fact]
    public async Task AnAcceptanceOfVersion1_SendsNothingToAnyProvider_UntilVersion2IsAccepted()
    {
        var gemini = new ScriptedProvider("gemini", "gemini-flash-latest", ScriptedProvider.Result(LlmOutcome.Error));
        var groq = new ScriptedProvider("groq", "openai/gpt-oss-120b");
        _kit.Chain(AiChains.Assistant, gemini, groq);
        Accept(version: 1);

        foreach (var feature in new[] { LlmFeatures.Chat, LlmFeatures.Categorize })
        {
            _kit.Chain(LlmFeatures.ChainOf(feature), gemini, groq);
            var blocked = await Gateway().GenerateAsync<TestAnswer>(_coupleId, LlmGatewayTestKit.Request(feature), LlmCallMode.Interactive, CancellationToken.None);
            Assert.Equal(LlmGatewayOutcome.NotConsented, blocked.Outcome);
        }

        Assert.Equal((0, 0), (gemini.Calls, groq.Calls));
        Assert.Empty(_kit.Rows());

        Accept(version: 2);
        var answered = await Gateway().GenerateAsync<TestAnswer>(_coupleId, LlmGatewayTestKit.Request(), LlmCallMode.Interactive, CancellationToken.None);

        Assert.Equal((LlmGatewayOutcome.Ok, "groq"), (answered.Outcome, answered.Provider));
        Assert.Equal((1, 1), (gemini.Calls, groq.Calls));
    }
}
