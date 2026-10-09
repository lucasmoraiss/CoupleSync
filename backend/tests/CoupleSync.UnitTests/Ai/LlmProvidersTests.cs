using System.Net;
using System.Text;
using System.Text.Json;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Integrations.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Issue #37 — the provider adapters against a fake HTTP server (an HttpMessageHandler: no test talks to a real
/// provider and no real key exists here), the fake provider with its start-up guard, the catalog and the reading of
/// the "Ai" configuration. Keys and hosts are invented.
/// </summary>
[Trait("Category", "Ai")]
public sealed class LlmProvidersTests
{
    private const string FakeKey = "fake-key-not-real-0a1b2c";

    private static LlmRequest Request(IReadOnlyList<LlmAttachment>? attachments = null) => new(
        LlmFeatures.Chat,
        "Regras do assistente.",
        [new LlmMessage("user", "Pergunta antiga"), new LlmMessage("model", "Resposta antiga"), new LlmMessage("user", "Quanto gastamos?")],
        LlmJsonSchema.Object(
            "answer",
            ("answer", LlmJsonSchema.String()),
            ("refs", LlmJsonSchema.Array(LlmJsonSchema.String())),
            ("kind", LlmJsonSchema.String("a", "b")),
            ("n", LlmJsonSchema.Integer()),
            ("v", LlmJsonSchema.Number()),
            ("ok", LlmJsonSchema.Boolean())),
        0.2m,
        1000,
        attachments);

    // ---------------------------------------------------------------- Gemini

    private static GeminiLlmProvider Gemini(FakeLlmServer server, string model = "gemini-flash-latest")
        => new(new FakeLlmServer.Factory(server, GeminiLlmProvider.HttpClientName), "https://gemini.test/v1beta", FakeKey, model);

    private const string GeminiOkBody = """
        {
          "candidates": [ { "content": { "role": "model", "parts": [ { "text": "{\"answer\":\"Oi\",\"refs\":[]}" } ] }, "finishReason": "STOP" } ],
          "usageMetadata": { "promptTokenCount": 321, "candidatesTokenCount": 45, "thoughtsTokenCount": 5, "totalTokenCount": 371 }
        }
        """;

    [Fact]
    public async Task Gemini_AsksForJsonInTheSchema_WithTheKeyInTheHeader_AndReadsTextAndUsage()
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.OK, GeminiOkBody));
        var provider = Gemini(server);

        var result = await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(("gemini", "gemini-flash-latest"), (provider.Provider, provider.Model));
        Assert.Equal(LlmOutcome.Ok, result.Outcome);
        Assert.Equal("""{"answer":"Oi","refs":[]}""", result.Json);
        Assert.Equal((321, 50), (result.InputTokens, result.OutputTokens));
        Assert.Null(result.ErrorCode);

        var sent = Assert.Single(server.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://gemini.test/v1beta/models/gemini-flash-latest:generateContent", sent.Url);
        Assert.Equal(FakeKey, sent.Headers["x-goog-api-key"]);
        Assert.DoesNotContain(FakeKey, sent.Url);

        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("Regras do assistente.", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var contents = body.GetProperty("contents");
        Assert.Equal(["user", "model", "user"], contents.EnumerateArray().Select(c => c.GetProperty("role").GetString()));
        Assert.Equal("Quanto gastamos?", contents[2].GetProperty("parts")[0].GetProperty("text").GetString());

        var config = body.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal(0.2m, config.GetProperty("temperature").GetDecimal());
        // The models "think", and the thinking counts in the output limit: the answer keeps its 1,000 tokens and the
        // thinking gets room of its own on top (measured with the real key: 358 thinking tokens for a 66-token answer).
        Assert.Equal(1000 + GeminiOptions.DefaultThinkingHeadroomTokens, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.True(GeminiOptions.DefaultThinkingHeadroomTokens >= 1000);
        var schema = config.GetProperty("responseSchema");
        Assert.Equal("OBJECT", schema.GetProperty("type").GetString());
        string[] all = ["answer", "refs", "kind", "n", "v", "ok"];
        Assert.Equal(all, schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(all, schema.GetProperty("propertyOrdering").EnumerateArray().Select(r => r.GetString()));
        var properties = schema.GetProperty("properties");
        Assert.Equal("STRING", properties.GetProperty("answer").GetProperty("type").GetString());
        Assert.Equal("ARRAY", properties.GetProperty("refs").GetProperty("type").GetString());
        Assert.Equal("STRING", properties.GetProperty("refs").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(["a", "b"], properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("INTEGER", properties.GetProperty("n").GetProperty("type").GetString());
        Assert.Equal("NUMBER", properties.GetProperty("v").GetProperty("type").GetString());
        Assert.Equal("BOOLEAN", properties.GetProperty("ok").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Gemini_SendsAttachmentsInline_WithTheLastMessage()
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.OK, GeminiOkBody));

        await Gemini(server).GenerateAsync(Request([new LlmAttachment("application/pdf", [1, 2, 3])]), CancellationToken.None);

        var parts = JsonDocument.Parse(server.Requests[0].Body).RootElement.GetProperty("contents")[2].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("application/pdf", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
    }

    /// <summary>
    /// The body of a real 429 of the free tier (answer to the real key on 2026-10-08, model with no quota): ONE
    /// QuotaFailure with the per-day and the per-minute violations together, and a RetryInfo.
    /// </summary>
    private static string Gemini429(string? retryDelay)
    {
        var retryInfo = retryDelay is null ? string.Empty : """,{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"DELAY"}""".Replace("DELAY", retryDelay);
        return """
            {"error":{"code":429,"message":"You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits.","status":"RESOURCE_EXHAUSTED","details":[
              {"@type":"type.googleapis.com/google.rpc.Help","links":[{"description":"Learn more about Gemini API quotas","url":"https://ai.google.dev/gemini-api/docs/rate-limits"}]},
              {"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[
                {"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_requests","quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier","quotaDimensions":{"location":"global","model":"gemini-3.1-pro"}},
                {"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_requests","quotaId":"GenerateRequestsPerMinutePerProjectPerModel-FreeTier","quotaDimensions":{"model":"gemini-3.1-pro","location":"global"}},
                {"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_input_token_count","quotaId":"GenerateContentInputTokensPerModelPerMinute-FreeTier","quotaDimensions":{"model":"gemini-3.1-pro","location":"global"}},
                {"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_input_token_count","quotaId":"GenerateContentInputTokensPerModelPerDay-FreeTier","quotaDimensions":{"location":"global","model":"gemini-3.1-pro"}}]}RETRY_INFO]}}
            """.Replace("RETRY_INFO", retryInfo);
    }

    [Theory]
    // The wait the provider asks for is what tells the limits apart: hours = the day's quota...
    [InlineData("77800s", LlmOutcome.QuotaExhaustedDay, 77800.0)]
    [InlineData("601s", LlmOutcome.QuotaExhaustedDay, 601.0)]
    // ...seconds or a few minutes = the per-minute limit.
    [InlineData("31s", LlmOutcome.RateLimitedMinute, 31.0)]
    [InlineData("31.5s", LlmOutcome.RateLimitedMinute, 31.5)]
    [InlineData("0.25s", LlmOutcome.RateLimitedMinute, 0.25)]
    [InlineData("600s", LlmOutcome.RateLimitedMinute, 600.0)]
    public async Task Gemini_A429WithARetryDelay_IsClassifiedByTheDelay_NotByTheListOfViolations(string retryDelay, LlmOutcome expected, double seconds)
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.TooManyRequests, Gemini429(retryDelay)));

        var result = await Gemini(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal((expected, "HTTP_429"), (result.Outcome, result.ErrorCode));
        Assert.Equal(TimeSpan.FromSeconds(seconds), result.RetryAfter);
        Assert.Null(result.Json);
    }

    [Theory]
    // No RetryInfo: per-day and per-minute violations come together, so nothing says which limit it was. A pause.
    [InlineData(null)]
    [InlineData("""{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded.","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]}}""")]
    [InlineData("""{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Resource has been exhausted (e.g. check quota)."}}""")]
    [InlineData("""{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"soon"}]}}""")]
    [InlineData("""{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"-5s"}]}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task Gemini_A429WithoutAUsableRetryDelay_IsOnlyAPause(string? body)
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.TooManyRequests, body ?? Gemini429(null)));

        var result = await Gemini(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal((LlmOutcome.RateLimitedMinute, "HTTP_429"), (result.Outcome, result.ErrorCode));
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    // Cut by the output limit: the JSON is incomplete even when some text came. The link failed; the next one is tried.
    [InlineData("""{"candidates":[{"content":{"role":"model","parts":[{"text":"{\"answer\":\"Vocês gast"}]},"finishReason":"MAX_TOKENS"}],"usageMetadata":{"promptTokenCount":300,"candidatesTokenCount":40,"thoughtsTokenCount":960}}""")]
    [InlineData("""{"candidates":[{"content":{"role":"model"},"finishReason":"MAX_TOKENS"}],"usageMetadata":{"promptTokenCount":300,"thoughtsTokenCount":1000}}""")]
    public async Task Gemini_AnAnswerCutByTheOutputLimit_IsInvalidOutput_WithItsTokensCounted(string body)
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.OK, body));

        var result = await Gemini(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal((LlmOutcome.InvalidOutput, "MAX_TOKENS"), (result.Outcome, result.ErrorCode));
        Assert.Null(result.Json);
        // Thinking tokens are part of what was spent.
        Assert.Equal((300, 1000), (result.InputTokens, result.OutputTokens));
    }

    [Fact]
    public async Task Gemini_A503HighDemand_IsATransientError_NotAQuotaProblem()
    {
        // Body of the answer the real key got on 2026-10-08.
        const string body = """{"error":{"code":503,"message":"This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.","status":"UNAVAILABLE"}}""";
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.ServiceUnavailable, body));

        var result = await Gemini(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal((LlmOutcome.Error, "HTTP_503"), (result.Outcome, result.ErrorCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP_500")]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP_401")]
    [InlineData(HttpStatusCode.NotFound, "HTTP_404")]
    public async Task Gemini_AnyOtherHttpFailure_IsAnError_WithoutTheBody(HttpStatusCode status, string code)
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(status, """{"error":{"message":"detalhe que não vai para lugar nenhum"}}"""));

        var result = await Gemini(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal((LlmOutcome.Error, code), (result.Outcome, result.ErrorCode));
    }

    [Theory]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""")]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""")]
    [InlineData("isto não é JSON")]
    public async Task Gemini_AnAnswerWithoutText_IsInvalidOutput(string body)
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.OK, body));

        var result = await Gemini(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(LlmOutcome.InvalidOutput, result.Outcome);
    }

    [Fact]
    public async Task Gemini_NetworkFailureIsAnError_AndAnHttpTimeoutIsATimeout()
    {
        var down = new FakeLlmServer(_ => throw new HttpRequestException("connection refused"));
        Assert.Equal((LlmOutcome.Error, "NETWORK"), await OutcomeOf(Gemini(down)));

        var slow = new FakeLlmServer(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        Assert.Equal((LlmOutcome.Timeout, "TIMEOUT"), await OutcomeOf(Gemini(slow)));
    }

    private static async Task<(LlmOutcome, string?)> OutcomeOf(ILlmProvider provider)
    {
        var result = await provider.GenerateAsync(Request(), CancellationToken.None);
        return (result.Outcome, result.ErrorCode);
    }

    [Fact]
    public void TheDefaultGeminiModel_IsTheFlashLatestAlias_AndTheOldDefaultIsGoneFromTheCode()
    {
        Assert.Equal("gemini-flash-latest", new GeminiOptions().Model);

        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "CoupleSync.Infrastructure")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var old = "gemini-2.0" + "-flash";
        var files = Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count > 100, $"only {files.Count} files scanned");
        Assert.Empty(files.Where(f => File.ReadAllText(f).Contains(old, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------- OpenAI-compatible (ready, switched off)

    private static OpenAiCompatibleLlmProvider Compatible(FakeLlmServer server)
        => new(new FakeLlmServer.Factory(server, OpenAiCompatibleLlmProvider.HttpClientNameOf("groq")), "groq", "https://compat.test/openai/v1/", FakeKey, "openai/gpt-oss-120b");

    private const string CompatibleOkBody = """
        {
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "{\"answer\":\"Oi\",\"refs\":[]}" }, "finish_reason": "stop" } ],
          "usage": { "prompt_tokens": 210, "completion_tokens": 33, "total_tokens": 243 }
        }
        """;

    [Fact]
    public async Task OpenAiCompatible_AsksForAStrictJsonSchema_WithBearerKey_AndReadsContentAndUsage()
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(HttpStatusCode.OK, CompatibleOkBody));
        var provider = Compatible(server);

        var result = await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(("groq", "openai/gpt-oss-120b"), (provider.Provider, provider.Model));
        Assert.Equal(LlmOutcome.Ok, result.Outcome);
        Assert.Equal("""{"answer":"Oi","refs":[]}""", result.Json);
        Assert.Equal((210, 33), (result.InputTokens, result.OutputTokens));

        var sent = Assert.Single(server.Requests);
        Assert.Equal("https://compat.test/openai/v1/chat/completions", sent.Url);
        Assert.Equal($"Bearer {FakeKey}", sent.Headers["Authorization"]);

        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("openai/gpt-oss-120b", body.GetProperty("model").GetString());
        Assert.Equal(0.2m, body.GetProperty("temperature").GetDecimal());
        Assert.Equal(1000, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(
            [("system", "Regras do assistente."), ("user", "Pergunta antiga"), ("assistant", "Resposta antiga"), ("user", "Quanto gastamos?")],
            body.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!)));

        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var jsonSchema = format.GetProperty("json_schema");
        Assert.Equal("answer", jsonSchema.GetProperty("name").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        var schema = jsonSchema.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["answer", "refs", "kind", "n", "v", "ok"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("array", schema.GetProperty("properties").GetProperty("refs").GetProperty("type").GetString());
        Assert.Equal(["a", "b"], schema.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
        // The common subset only: nothing a strict provider would refuse.
        Assert.DoesNotContain("oneOf", sent.Body);
        Assert.DoesNotContain("$ref", sent.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached for model x on requests per minute (RPM): Limit 30"}}""", LlmOutcome.RateLimitedMinute)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached for model x on tokens per day (TPD): Limit 200000"}}""", LlmOutcome.QuotaExhaustedDay)]
    [InlineData(HttpStatusCode.TooManyRequests, "", LlmOutcome.RateLimitedMinute)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"bad schema"}}""", LlmOutcome.Error)]
    [InlineData(HttpStatusCode.OK, """{"choices":[]}""", LlmOutcome.InvalidOutput)]
    [InlineData(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":null}}]}""", LlmOutcome.InvalidOutput)]
    [InlineData(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"{\"answer\":\"cortad"},"finish_reason":"length"}]}""", LlmOutcome.InvalidOutput)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded"}}""", LlmOutcome.Error)]
    public async Task OpenAiCompatible_MapsFailuresToOutcomes(HttpStatusCode status, string body, LlmOutcome expected)
    {
        var server = new FakeLlmServer(_ => FakeLlmServer.Json(status, body));

        var result = await Compatible(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Theory]
    [InlineData("12", LlmOutcome.RateLimitedMinute, 12)]
    [InlineData("3600", LlmOutcome.QuotaExhaustedDay, 3600)]
    public async Task OpenAiCompatible_A429WithRetryAfter_IsClassifiedByTheWait(string retryAfter, LlmOutcome expected, int seconds)
    {
        var server = new FakeLlmServer(_ =>
        {
            var response = FakeLlmServer.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached"}}""");
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });

        var result = await Compatible(server).GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(seconds), result.RetryAfter);
    }

    [Fact]
    public void Catalog_GivesTheGeminiModelsTheConfiguredThinkingHeadroom()
    {
        var providers = new LlmProvidersOptions();
        AiConfiguration.Apply(providers, Config(("GEMINI_API_KEY", FakeKey)), new GeminiOptions { ThinkingHeadroomTokens = 512 });

        Assert.Equal(512, providers.GeminiThinkingHeadroomTokens);
        Assert.Equal(GeminiOptions.DefaultThinkingHeadroomTokens, new GeminiOptions().ThinkingHeadroomTokens);
    }

    // ---------------------------------------------------------------- the catalog: who is in a chain

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static LlmProviderCatalog Catalog(IConfiguration config)
    {
        var ai = new AiOptions();
        AiConfiguration.Apply(ai, config);
        var providers = new LlmProvidersOptions();
        AiConfiguration.Apply(providers, config, new GeminiOptions());
        return new LlmProviderCatalog(new FakeLlmServer.Factory(new FakeLlmServer(_ => throw new InvalidOperationException("no call expected")), null), Options.Create(ai), Options.Create(providers));
    }

    [Fact]
    public void Catalog_WithoutAnyKey_HasNoProviderAtAll()
    {
        var catalog = Catalog(Config());

        Assert.False(catalog.AnyAvailable);
        Assert.Null(catalog.Find("gemini", "gemini-flash-latest"));
        Assert.Null(catalog.Find("groq", "openai/gpt-oss-120b"));
        Assert.Null(catalog.Find("fake", "fake"));
    }

    [Fact]
    public void Catalog_WithTheGeminiKey_OffersAnyGeminiModel()
    {
        var catalog = Catalog(Config(("GEMINI_API_KEY", FakeKey)));

        Assert.True(catalog.AnyAvailable);
        var provider = Assert.IsType<GeminiLlmProvider>(catalog.Find("gemini", "gemini-3-flash-preview"));
        Assert.Equal("gemini-3-flash-preview", provider.Model);
        Assert.Null(catalog.Find("fake", "fake"));
    }

    [Fact]
    public void Catalog_AnOpenAiCompatibleProviderWithoutKey_IsInNoChain_AndWithKeyItIsThere()
    {
        (string, string?)[] configured =
        [
            ("Ai:OpenAiCompatible:0:Name", "groq"),
            ("Ai:OpenAiCompatible:0:BaseUrl", "https://compat.test/openai/v1"),
            ("Ai:OpenAiCompatible:0:ApiKeyVariable", "GROQ_API_KEY"),
        ];

        var withoutKey = Catalog(Config(configured));
        Assert.Null(withoutKey.Find("groq", "openai/gpt-oss-120b"));
        Assert.False(withoutKey.AnyAvailable);

        var emptyKey = Catalog(Config([.. configured, ("GROQ_API_KEY", "  ")]));
        Assert.Null(emptyKey.Find("groq", "openai/gpt-oss-120b"));

        var withKey = Catalog(Config([.. configured, ("GROQ_API_KEY", FakeKey)]));
        var provider = Assert.IsType<OpenAiCompatibleLlmProvider>(withKey.Find("GROQ", "openai/gpt-oss-120b"));
        Assert.Equal(("groq", "openai/gpt-oss-120b"), (provider.Provider, provider.Model));
        // A key alone does not create a provider: it must be configured by name.
        Assert.Null(withKey.Find("mistral", "mistral-small"));
    }

    // ---------------------------------------------------------------- configuration

    [Fact]
    public void Configuration_Defaults_AreTheChainsAndBudgetsOfTheDesign_WithOnlyGeminiFlashModels()
    {
        var options = new AiOptions();
        AiConfiguration.Apply(options, Config());

        Assert.False(options.Disabled);
        Assert.False(options.UseFakeProvider);
        Assert.Equal((25, 60_000L, 60, 150), (options.GroupDailyCalls, options.GroupDailyTokens, options.JobDailyCalls, options.GlobalDailyInteractiveCalls));
        Assert.Equal((20, 30, 60, 60), (options.InteractiveBudget.TotalSeconds, options.LinkTimeout.TotalSeconds, options.JobLinkTimeout.TotalSeconds, options.JobMaxWait.TotalSeconds));

        // Measured with the real key (2026-10-08): gemini-flash-latest is the most capable Flash that answers
        // (it resolves to 3.8); every identifier below is a different model, with a quota of its own.
        // Volume first: the Assistant, the daily insight and the categorization never use the two models of the summaries.
        string[] fast = ["gemini|gemini-flash-lite-latest", "gemini|gemini-2.5-flash", "gemini|gemini-3.1-flash-lite"];
        // Quality first: few calls, and the text matters.
        string[] quality = ["gemini|gemini-flash-latest", "gemini|gemini-3-flash-preview", "gemini|gemini-flash-lite-latest"];
        Assert.Equal(fast, Links(options, AiChains.Assistant));
        Assert.Equal(fast, Links(options, AiChains.Categorize));
        Assert.Equal(fast, Links(options, AiChains.Daily));
        Assert.Equal(quality, Links(options, AiChains.Weekly));
        Assert.Equal(quality, Links(options, AiChains.Education));
        Assert.Equal(5, options.Chains.Count);
        Assert.Empty(fast.Intersect(quality.Take(2)));

        // Pace per minute, per model, replaceable in Ai__Limits: 5 for the models that think; 10 for the "lite" ones
        // (seven calls in a row to gemini-flash-lite-latest went through with the real key). Daily quotas: unknown.
        Assert.Equal(5, options.LimitFor("gemini", "gemini-flash-latest").Rpm);
        Assert.Equal(5, options.LimitFor("gemini", "gemini-3-flash-preview").Rpm);
        Assert.Equal(5, options.LimitFor("gemini", "gemini-2.5-flash").Rpm);
        Assert.Equal(10, options.LimitFor("gemini", "gemini-flash-lite-latest").Rpm);
        Assert.Equal(10, options.LimitFor("gemini", "gemini-3.1-flash-lite").Rpm);
        Assert.All(options.Chains.Values.SelectMany(c => c), link => Assert.Null(options.LimitFor(link.Provider, link.Model).Rpd));
        Assert.All(options.Chains.Values.SelectMany(c => c), link =>
        {
            Assert.Equal("gemini", link.Provider);
            Assert.Contains("flash", link.Model);
        });
    }

    private static IEnumerable<string> Links(AiOptions options, string chain) => options.Chains[chain].Select(l => $"{l.Provider}|{l.Model}");

    [Fact]
    public void Configuration_ReadsIndexedChainsLimitsAndBudgets_AndAnEmptyLimitIsUnknown()
    {
        var options = new AiOptions();
        AiConfiguration.Apply(options, Config(
            ("Ai:Disabled", "true"),
            ("Ai:UseFakeProvider", "TRUE"),
            ("Ai:GroupDailyCalls", "7"),
            ("Ai:GroupDailyTokens", "1234"),
            ("Ai:JobDailyCalls", "8"),
            ("Ai:GlobalDailyInteractiveCalls", "9"),
            ("Ai:Chains:Assistant:0", "gemini|gemini-2.5-flash"),
            ("Ai:Chains:Assistant:1", " groq | openai/gpt-oss-120b "),
            ("Ai:Chains:Assistant:2", "sem-separador"),
            ("Ai:Limits:0:Provider", "gemini"),
            ("Ai:Limits:0:Model", "gemini-flash-latest"),
            ("Ai:Limits:0:Rpd", ""),
            ("Ai:Limits:0:Tpd", ""),
            ("Ai:Limits:0:Rpm", "5"),
            ("Ai:Limits:0:Tpm", ""),
            ("Ai:Limits:1:Provider", "groq"),
            ("Ai:Limits:1:Model", "openai/gpt-oss-120b"),
            ("Ai:Limits:1:Rpd", "1000"),
            ("Ai:Limits:1:Tpd", "200000"),
            ("Ai:Limits:1:Rpm", "30"),
            ("Ai:Limits:1:Tpm", "8000")));

        Assert.True(options.Disabled);
        Assert.True(options.UseFakeProvider);
        Assert.Equal((7, 1234L, 8, 9), (options.GroupDailyCalls, options.GroupDailyTokens, options.JobDailyCalls, options.GlobalDailyInteractiveCalls));
        // The configured chain replaces the default one; a malformed link is dropped; the other chains keep their default.
        Assert.Equal(["gemini|gemini-2.5-flash", "groq|openai/gpt-oss-120b"], Links(options, AiChains.Assistant));
        Assert.Equal(3, options.Chains[AiChains.Weekly].Count);

        var gemini = options.LimitFor("gemini", "gemini-flash-latest");
        Assert.Null(gemini.Rpd);
        Assert.Null(gemini.Tpd);
        Assert.Null(gemini.Tpm);
        Assert.Equal(5, gemini.Rpm);

        var groq = options.LimitFor("groq", "openai/gpt-oss-120b");
        Assert.Equal((1000, 200_000L, 30, 8000L), (groq.Rpd!.Value, groq.Tpd!.Value, groq.Rpm!.Value, groq.Tpm!.Value));

        // A model nobody configured: Gemini gets the pacing default, another provider gets no limit at all.
        Assert.Equal(5, options.LimitFor("gemini", "gemini-9-flash").Rpm);
        Assert.Null(options.LimitFor("outro", "modelo").Rpm);
    }

    // ---------------------------------------------------------------- the fake provider and its guard

    [Fact]
    public async Task Fake_BuildsItsAnswerFromTheRequestedSchema_InPortuguese_WithoutNumbers_AndPassesTheValidators()
    {
        var provider = new FakeLlmProvider();
        var request = Request();

        var result = await provider.GenerateAsync(request, CancellationToken.None);

        Assert.Equal(("fake", "fake"), (provider.Provider, provider.Model));
        Assert.Equal(LlmOutcome.Ok, result.Outcome);
        Assert.True(result.InputTokens > 0 && result.OutputTokens > 0);

        var json = JsonDocument.Parse(result.Json!).RootElement;
        Assert.True(request.ResponseSchema.Matches(json), result.Json);
        Assert.Equal("a", json.GetProperty("kind").GetString());
        Assert.Empty(json.GetProperty("refs").EnumerateArray());

        var answer = json.GetProperty("answer").GetString()!;
        Assert.Equal(FakeLlmProvider.FixedText, answer);
        Assert.DoesNotContain(answer, char.IsDigit);
        Assert.True(OutputSafetyValidator.Validate(answer).IsValid);
        Assert.True(NumberGroundingValidator.Validate(answer, JsonDocument.Parse("{}").RootElement, []).IsValid);
    }

    [Fact]
    public void FakeGuard_LetsTheApiStart_WhenTheFakeIsOff_OrOnWithNoRealKeyAndNotOnRender()
    {
        FakeLlmProviderGuard.EnsureSafe(Config(("GEMINI_API_KEY", FakeKey), ("RENDER", "true")));
        FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "false"), ("GEMINI_API_KEY", FakeKey)));
        FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "true")));
        FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "true"), ("GEMINI_API_KEY", " "), ("GROQ_API_KEY", FakeKey)));
    }

    [Fact]
    public void FakeGuard_RefusesToStart_WithTheFakeOnAndARealKeyOrOnRender()
    {
        (string, string?)[][] unsafeSetups =
        [
            [("GEMINI_API_KEY", FakeKey)],
            [("Gemini:ApiKey", FakeKey)],
            [("RENDER", "true")],
            [("Ai:OpenAiCompatible:0:Name", "groq"), ("Ai:OpenAiCompatible:0:ApiKeyVariable", "GROQ_API_KEY"), ("GROQ_API_KEY", FakeKey)],
        ];

        foreach (var setup in unsafeSetups)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => FakeLlmProviderGuard.EnsureSafe(Config([("Ai:UseFakeProvider", "true"), .. setup])));
            Assert.Contains("Ai__UseFakeProvider", ex.Message);
            Assert.DoesNotContain(FakeKey, ex.Message);
        }

        // The key the API would really use (after every override) counts too.
        Assert.Throws<InvalidOperationException>(() => FakeLlmProviderGuard.EnsureSafe(Config(("Ai:UseFakeProvider", "true")), effectiveGeminiKey: FakeKey));
    }
}

/// <summary>A provider's HTTP API in memory: records what was sent and answers what the test decides.</summary>
internal sealed class FakeLlmServer : HttpMessageHandler
{
    private readonly Func<Recorded, HttpResponseMessage> _answer;

    public FakeLlmServer(Func<Recorded, HttpResponseMessage> answer) => _answer = answer;

    public List<Recorded> Requests { get; } = new();

    public static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new Recorded(request.Method.Method, request.RequestUri!.ToString(), headers, body);
        Requests.Add(recorded);
        return _answer(recorded);
    }

    internal sealed record Recorded(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string Body);

    internal sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string? _expectedName;

        public Factory(HttpMessageHandler handler, string? expectedName)
        {
            _handler = handler;
            _expectedName = expectedName;
        }

        public HttpClient CreateClient(string name)
        {
            if (_expectedName is not null) Assert.Equal(_expectedName, name);
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
