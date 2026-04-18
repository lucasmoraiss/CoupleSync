using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        Assert.Equal(FakeChatGeminiAdapter.FixedReply, payload!.Reply);
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
        // Each factory gets its own singleton ChatRateLimiter
        await using var factory = new ChatWebApplicationFactory(enabled: true);
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

    private sealed record ChatResponseDto(string Reply);

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);
}

// ── Fake Gemini adapter ────────────────────────────────────────────────────

internal sealed class FakeChatGeminiAdapter : IGeminiAdapter
{
    public const string FixedReply = "Seus gastos estão sob controle!";

    public Task<string> SendAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        CancellationToken ct)
        => Task.FromResult(FixedReply);
}

// ── WebApplicationFactory ──────────────────────────────────────────────────

internal sealed class ChatWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly bool _enabled;

    private readonly string _databaseConnectionString =
        $"Data Source=couplesync-chat-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _keepAliveConnection;

    public ChatWebApplicationFactory(bool enabled)
    {
        _enabled = enabled;
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        return base.CreateHost(builder);
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
                ["Gemini:ApiKey"] = "test-key"
            };
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

            // Replace real Gemini adapter with fake (no external API calls)
            services.RemoveAll<IGeminiAdapter>();
            services.AddScoped<IGeminiAdapter, FakeChatGeminiAdapter>();

            // Override GeminiOptions.Enabled to match the test scenario
            services.PostConfigure<GeminiOptions>(opts =>
            {
                opts.Enabled = _enabled;
                opts.ApiKey = "test-key";
            });

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
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
