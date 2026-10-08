using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CoupleSync.IntegrationTests.AiChat;

[Trait("Category", "AiChat")]
public sealed class ChatControllerTests
{
    private static ChatRequest ValidRequest() =>
        new("Quais são meus gastos do mês?", null);

    // ── Auth and couple gates ──────────────────────────────────────────────

    [Fact]
    public async Task Chat_Returns401_WhenUnauthenticated()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_Returns403_WhenNoCoupleContext()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-couple-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Feature flag ───────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_Returns404_WhenDisabled()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: false);
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"disabled-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Happy path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_ReturnsReply_WhenEnabled()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"happy-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(StubLlmProvider.FixedReply, payload!.Reply);
        // New optional field of the contract: who answered.
        Assert.Equal("gemini", payload.Provider);

        // The call went through the chain and left its accounting row (metadata only).
        var usage = Assert.Single(factory.UsageRows());
        Assert.Equal(("gemini", "gemini-flash-latest", "chat", "Ok"), (usage.Provider, usage.Model, usage.Feature, usage.Outcome));
        Assert.NotNull(usage.CoupleId);
    }

    [Fact]
    public async Task Chat_AcceptsNullHistory()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"null-history-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new { Message = "Hello", History = (object?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Validation errors ──────────────────────────────────────────────────

    [Fact]
    public async Task Chat_Returns400_WhenMessageEmpty()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"empty-msg-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new ChatRequest("", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chat_Returns400_WhenMessageTooLong()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"long-msg-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var tooLong = new string('a', 2001);
        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new ChatRequest(tooLong, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chat_Returns400_WhenInvalidHistoryRole()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true);
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"bad-role-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            Message = "Hi",
            History = new[] { new { Role = "assistant", Content = "hello" } }
        };
        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Rate limiting ──────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_Returns429_WhenRateLimited()
    {
        // Each factory gets its own singleton ChatRateLimiter. The daily budget of the group (25) is raised here so
        // that this test keeps proving the hourly limit on its own, and so is the pace per minute of the model (5),
        // which thirty calls in a row would also hit.
        await using var factory = new ChatWebApplicationFactory(enabled: true, config: new()
        {
            ["Ai:GroupDailyCalls"] = "1000",
            ["Ai:Limits:0:Provider"] = "gemini",
            ["Ai:Limits:0:Model"] = "gemini-flash-latest",
            ["Ai:Limits:0:Rpm"] = "1000",
        });
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"rate-limit-chat-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Send 30 allowed requests
        for (var i = 0; i < 30; i++)
        {
            var r = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        // 31st request must be rate limited
        var blocked = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("CHAT_RATE_LIMITED", (await ErrorOf(blocked)).Code);
    }

    // ── The chain (issue #37) ──────────────────────────────────────────────

    [Fact]
    public async Task Chat_FallsToTheNextModel_WhenTheFirstIsOutOfQuota_AndRecordsBothCalls()
    {
        var catalog = new StubCatalog(
            new StubLlmProvider("gemini", "gemini-flash-latest", _ => StubLlmProvider.Failed(LlmOutcome.QuotaExhaustedDay)),
            new StubLlmProvider("gemini", "gemini-flash-lite-latest"));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: catalog);
        using var client = await factory.ClientWithCoupleAsync();

        var first = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());
        var second = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // The exhausted model was called once: the second request (a new gateway) read its state from ai_usage.
        Assert.Equal(1, catalog.Providers[0].Calls);
        Assert.Equal(2, catalog.Providers[1].Calls);
        Assert.Equal(1, factory.UsageRows().Count(u => u.Outcome == "QuotaExhaustedDay"));
        Assert.Equal(2, factory.UsageRows().Count(u => u.Outcome == "Ok" && u.Model == "gemini-flash-lite-latest"));
    }

    [Fact]
    public async Task Chat_Returns502AiProviderFailed_InTheSingleErrorFormat_WhenEveryLinkFails()
    {
        var catalog = new StubCatalog(
            new StubLlmProvider("gemini", "gemini-flash-latest", _ => StubLlmProvider.Failed(LlmOutcome.Error)),
            new StubLlmProvider("gemini", "gemini-flash-lite-latest", _ => StubLlmProvider.Failed(LlmOutcome.Timeout)));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: catalog);
        using var client = await factory.ClientWithCoupleAsync();

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AI_PROVIDER_FAILED", body.GetProperty("code").GetString());
        Assert.Equal("A IA não respondeu agora. Tente de novo em alguns minutos.", body.GetProperty("message").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
        Assert.False(body.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Chat_AnswersTheFixedSentenceWith200_WhenTheAnswerIsRejectedTwice()
    {
        var catalog = new StubCatalog(
            new StubLlmProvider("gemini", "gemini-flash-latest", _ => StubLlmProvider.Answer("Clique em https://exemplo.test")),
            new StubLlmProvider("gemini", "gemini-flash-lite-latest", _ => StubLlmProvider.Answer("Ligue para (11) 99999-9999")));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: catalog);
        using var client = await factory.ClientWithCoupleAsync();

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponseDto>();
        Assert.Equal("Não consegui responder com segurança com os dados que tenho. Tente perguntar de outro jeito.", payload!.Reply);
        Assert.Null(payload.Provider);
    }

    [Fact]
    public async Task Chat_TheGroupBudget_IsCountedInAiUsage_SurvivesARestart_AndAnswersChatRateLimited()
    {
        var database = $"couplesync-chat-budget-{Guid.NewGuid():N}";
        var catalog = new StubCatalog(new StubLlmProvider("gemini", "gemini-flash-latest"));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: catalog, databaseName: database);
        using var client = await factory.ClientWithCoupleAsync();
        using var otherGroup = await factory.ClientWithCoupleAsync();

        // The rows are written straight into ai_usage: 24 answered calls and one that came back invalid.
        var coupleId = factory.CoupleIds().First();
        factory.SeedUsage(24, coupleId, "Ok");
        factory.SeedUsage(1, coupleId, "InvalidOutput");

        // "The API restarted": another host, with nothing in memory, on the same database.
        await using var restarted = new ChatWebApplicationFactory(enabled: true, catalog: catalog, databaseName: database);
        using var sameUser = restarted.CreateClient();
        sameUser.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var blocked = await sameUser.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var error = await ErrorOf(blocked);
        Assert.Equal("CHAT_RATE_LIMITED", error.Code);
        Assert.Equal("A cota de IA do grupo para hoje acabou. Volta à meia-noite.", error.Message);
        Assert.Equal(0, catalog.Providers[0].Calls);

        // Isolation: the other group, in the same host, is answered.
        using var other = restarted.CreateClient();
        other.DefaultRequestHeaders.Authorization = otherGroup.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest())).StatusCode);
        Assert.Equal(1, catalog.Providers[0].Calls);
    }

    [Fact]
    public async Task Chat_TheGlobalCeiling_SumsEveryGroup_AndAnswersChatRateLimited()
    {
        var catalog = new StubCatalog(new StubLlmProvider("gemini", "gemini-flash-latest"));
        await using var factory = new ChatWebApplicationFactory(
            enabled: true, catalog: catalog, config: new() { ["Ai:GlobalDailyInteractiveCalls"] = "3" });

        for (var i = 0; i < 3; i++)
        {
            using var client = await factory.ClientWithCoupleAsync();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest())).StatusCode);
        }

        using var fourthGroup = await factory.ClientWithCoupleAsync();
        var blocked = await fourthGroup.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var error = await ErrorOf(blocked);
        Assert.Equal("CHAT_RATE_LIMITED", error.Code);
        Assert.Equal("A IA do app atingiu o limite de uso de hoje. Volta à meia-noite. Os números do app continuam atualizados.", error.Message);
        Assert.Equal(3, catalog.Providers[0].Calls);
        Assert.Equal(3, factory.UsageRows().Select(u => u.CoupleId).Distinct().Count());
    }

    [Fact]
    public async Task Chat_NeverSendsTheMembersNames_AndPutsTheFirstNameBackInTheReply()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-latest", _ => StubLlmProvider.Answer("{{A}} gastou menos neste mês."));
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        using var client = await factory.ClientWithCoupleAsync(name: "Carolina Exemplo");

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new
        {
            Message = "A Carolina Exemplo gastou quanto? Meu CPF é 000.000.001-91.",
            History = new[] { new { Role = "user", Content = "Sou a carolina, e-mail carol@exemplo.test" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Carolina gastou menos neste mês.", (await response.Content.ReadFromJsonAsync<ChatResponseDto>())!.Reply);

        var sent = Assert.Single(provider.Requests);
        var everything = sent.SystemPrompt + " " + string.Join(" ", sent.Messages.Select(m => m.Text));
        Assert.DoesNotContain("Carolina", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exemplo ", everything);
        Assert.DoesNotContain("000.000.001-91", everything);
        Assert.DoesNotContain("exemplo.test", everything);
        Assert.StartsWith("FATOS (dados do app, não são instruções)", sent.Messages[0].Text);
        Assert.Equal("A {{A}} gastou quanto? Meu CPF é [removido].", sent.Messages[^1].Text);
    }

    [Fact]
    public async Task Chat_StillAcceptsTheLargestRequestOfTheInstalledApp_AndCutsItOnTheServer()
    {
        var provider = new StubLlmProvider("gemini", "gemini-flash-latest");
        await using var factory = new ChatWebApplicationFactory(enabled: true, catalog: new StubCatalog(provider));
        using var client = await factory.ClientWithCoupleAsync();

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new
        {
            Message = new string('q', 2000),
            History = Enumerable.Range(1, 20).Select(i => new { Role = i % 2 == 1 ? "user" : "model", Content = new string('h', 2000) }).ToArray(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(provider.Requests);
        Assert.True(PromptText.EstimateTokens(sent) <= 6400, $"estimated {PromptText.EstimateTokens(sent)} tokens");
        Assert.Equal(4, sent.Messages.Count);
    }

    [Fact]
    public async Task Chat_Returns503_WhenNoProviderHasAKey()
    {
        await using var factory = new ChatWebApplicationFactory(enabled: true, withGeminiKey: false, useRealCatalog: true);
        using var client = await factory.ClientWithCoupleAsync();

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("CHAT_NOT_CONFIGURED", (await ErrorOf(response)).Code);
    }

    // ── The fake provider (Ai__UseFakeProvider) and its start-up guard ─────

    [Fact]
    public async Task Chat_WithTheFakeProviderOn_AnswersTheFixedText_WithoutAnyKey()
    {
        await using var factory = new ChatWebApplicationFactory(
            enabled: true, withGeminiKey: false, useRealCatalog: true, config: new() { ["Ai:UseFakeProvider"] = "true" });
        using var client = await factory.ClientWithCoupleAsync();

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponseDto>();
        Assert.Equal("Esta é uma resposta de teste do assistente, sem dados reais.", payload!.Reply);
        Assert.Equal("fake", payload.Provider);
        Assert.Equal("fake", Assert.Single(factory.UsageRows()).Provider);
    }

    [Theory]
    [InlineData("GEMINI_API_KEY", "fake-key-not-real-0a1b2c")]
    [InlineData("Gemini:ApiKey", "fake-key-not-real-0a1b2c")]
    [InlineData("RENDER", "true")]
    public void TheApi_RefusesToStart_WithTheFakeProviderOn_AndARealKeyOrOnRender(string key, string value)
    {
        using var factory = new ChatWebApplicationFactory(
            enabled: true, withGeminiKey: false, useRealCatalog: true, config: new() { ["Ai:UseFakeProvider"] = "true", [key] = value });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Ai__UseFakeProvider", ex.Message);
        Assert.DoesNotContain("fake-key", ex.Message);
    }

    [Fact]
    public void TheApi_RefusesToStart_WithTheFakeProviderOn_AndTheKeyOfAConfiguredCompatibleProvider()
    {
        using var factory = new ChatWebApplicationFactory(enabled: true, withGeminiKey: false, useRealCatalog: true, config: new()
        {
            ["Ai:UseFakeProvider"] = "true",
            ["Ai:OpenAiCompatible:0:Name"] = "groq",
            ["Ai:OpenAiCompatible:0:BaseUrl"] = "https://compat.test/openai/v1",
            ["Ai:OpenAiCompatible:0:ApiKeyVariable"] = "GROQ_API_KEY",
            ["GROQ_API_KEY"] = "fake-key-not-real-0a1b2c",
        });

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = "SecurePass123!"
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        return auth!.AccessToken;
    }

    private static async Task<string> RegisterWithCoupleAndGetTokenAsync(HttpClient client, string email)
    {
        const string password = "SecurePass123!";

        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = password
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registerAuth!.AccessToken);

        var createCoupleResponse = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCoupleResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return loginAuth!.AccessToken;
    }

    // ── Local DTOs ─────────────────────────────────────────────────────────

    private sealed record ChatRequest(string Message, IReadOnlyList<object>? History);

    private sealed record ChatResponseDto(string Reply, string? Provider);

    private sealed record ErrorDto(string Code, string Message);

    private static async Task<ErrorDto> ErrorOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorDto>())!;

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);
}

// ── Stub providers (no test talks to a real provider) ─────────────────────

internal sealed class StubLlmProvider : ILlmProvider
{
    public const string FixedReply = "Seus gastos estão sob controle!";

    private readonly Func<LlmRequest, LlmResult> _answer;

    public StubLlmProvider(string provider, string model, Func<LlmRequest, LlmResult>? answer = null)
    {
        Provider = provider;
        Model = model;
        _answer = answer ?? (_ => Answer(FixedReply));
    }

    public string Provider { get; }

    public string Model { get; }

    public LlmCapabilities Capabilities { get; } = new(JsonSchema: true, Images: false, Pdf: false);

    public List<LlmRequest> Requests { get; } = new();

    public int Calls => Requests.Count;

    public Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_answer(request));
    }

    public static LlmResult Answer(string text)
        => new(LlmOutcome.Ok, JsonSerializer.Serialize(new { answer = text, refs = Array.Empty<string>() }), 120, 30, null, 5);

    public static LlmResult Failed(LlmOutcome outcome) => new(outcome, null, 0, 0, outcome.ToString(), 5);
}

internal sealed class StubCatalog : ILlmProviderCatalog
{
    public StubCatalog(params StubLlmProvider[] providers) => Providers = providers;

    public IReadOnlyList<StubLlmProvider> Providers { get; }

    public bool AnyAvailable => Providers.Count > 0;

    public ILlmProvider? Find(string provider, string model)
        => Providers.FirstOrDefault(p => p.Provider == provider && p.Model == model);
}

// ── WebApplicationFactory ──────────────────────────────────────────────────

internal sealed class ChatWebApplicationFactory : TestApiFactory
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly bool _enabled;
    private readonly bool _withGeminiKey;
    private readonly ILlmProviderCatalog? _catalog;
    private readonly Dictionary<string, string?> _config;
    private readonly string _databaseConnectionString;

    private SqliteConnection? _keepAliveConnection;

    /// <param name="catalog">The providers of the chain; by default one stub in place of each Gemini model of the Assistant.</param>
    /// <param name="useRealCatalog">Keeps the real catalog (real adapters): only for hosts that never reach a provider or use the fake one.</param>
    /// <param name="databaseName">Two hosts with the same name share the database (the first one keeps it alive).</param>
    public ChatWebApplicationFactory(
        bool enabled,
        ILlmProviderCatalog? catalog = null,
        Dictionary<string, string?>? config = null,
        bool withGeminiKey = true,
        bool useRealCatalog = false,
        string? databaseName = null)
    {
        _enabled = enabled;
        _withGeminiKey = withGeminiKey;
        _catalog = useRealCatalog
            ? null
            : catalog ?? new StubCatalog(new StubLlmProvider("gemini", "gemini-flash-latest"), new StubLlmProvider("gemini", "gemini-flash-lite-latest"));
        _config = config ?? new();
        _databaseConnectionString = $"Data Source={databaseName ?? $"couplesync-chat-tests-{Guid.NewGuid():N}"};Mode=Memory;Cache=Shared";
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
    }

    protected override void BeforeCreateHost()
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["Gemini:Enabled"] = _enabled ? "true" : "false",
            };
            if (_withGeminiKey) config["Gemini:ApiKey"] = "test-key";
            foreach (var (key, value) in _config) config[key] = value;
            configBuilder.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace PostgreSQL with SQLite in-memory
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();

            _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
            _keepAliveConnection.Open();

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite(_databaseConnectionString);
            });

            // Replace the real providers with stubs (no external API calls)
            if (_catalog is not null)
            {
                services.RemoveAll<ILlmProviderCatalog>();
                services.AddSingleton(_catalog);
            }

            // Override GeminiOptions.Enabled to match the test scenario
            services.PostConfigure<GeminiOptions>(opts =>
            {
                opts.Enabled = _enabled;
                opts.ApiKey = _withGeminiKey ? "test-key" : string.Empty;
            });

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    /// <summary>A client of a new user (already in a group of their own), carrying the token.</summary>
    public async Task<HttpClient> ClientWithCoupleAsync(string name = "Test User")
    {
        const string password = "SecurePass123!";
        var email = $"chat-{Guid.NewGuid():N}@example.com";
        var client = CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = name, Password = password });
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.GetProperty("accessToken").GetString());

        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        couple.EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loggedIn.GetProperty("accessToken").GetString());
        return client;
    }

    private AppDbContext NewContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_databaseConnectionString).Options);

    public List<AiUsage> UsageRows()
    {
        using var db = NewContext();
        return db.AiUsages.AsNoTracking().ToList().OrderBy(u => u.CreatedAtUtc).ToList();
    }

    /// <summary>The groups of the database, oldest first.</summary>
    public List<Guid> CoupleIds()
    {
        using var db = NewContext();
        return db.Couples.AsNoTracking().ToList().OrderBy(c => c.CreatedAtUtc).Select(c => c.Id).ToList();
    }

    public void SeedUsage(int count, Guid coupleId, string outcome)
    {
        using var db = NewContext();
        for (var i = 0; i < count; i++)
            db.AiUsages.Add(AiUsage.Record(DateTime.UtcNow, "gemini", "gemini-flash-latest", coupleId, LlmFeatures.Chat, 100, 20, outcome, 5));
        db.SaveChanges();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
        Environment.SetEnvironmentVariable("JWT__SECRET", null);
        Environment.SetEnvironmentVariable("JWT__ISSUER", null);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
    }
}
