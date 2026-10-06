using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.IntegrationTests.Security;

/// <summary>
/// A05 — brute-force protection: 5 requests per minute per client IP on login/register and
/// 5 per minute per authenticated user on couples/join. The 6th request gets 429 in the
/// standard error envelope.
/// </summary>
[Trait("Category", "RateLimiting")]
public sealed class RateLimitingIntegrationTests
{
    private const string Password = "SecurePass123!";

    [Fact]
    public async Task Login_SixthAttemptWithinAMinute_Returns429InStandardEnvelope()
    {
        await using var factory = new RateLimitWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await LoginAsync(client, "nobody@example.com", "WrongPass123!");
            Assert.True(HttpStatusCode.Unauthorized == response.StatusCode, $"attempt {attempt}: got {(int)response.StatusCode}");
        }

        var sixth = await LoginAsync(client, "nobody@example.com", "WrongPass123!");

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        var error = await sixth.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("RATE_LIMIT_EXCEEDED", error!.Code);
        Assert.Equal("Muitas tentativas. Aguarde um instante e tente novamente.", error.Message);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
        Assert.True(sixth.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Register_SixthAttemptWithinAMinute_Returns429()
    {
        await using var factory = new RateLimitWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await RegisterAsync(client, $"rl-{Guid.NewGuid():N}@example.com");
            Assert.True(HttpStatusCode.Created == response.StatusCode, $"attempt {attempt}: got {(int)response.StatusCode}");
        }

        var sixth = await RegisterAsync(client, $"rl-{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        var error = await sixth.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("RATE_LIMIT_EXCEEDED", error!.Code);
    }

    [Fact]
    public async Task Login_And_Register_HaveIndependentBudgets()
    {
        await using var factory = new RateLimitWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
            await LoginAsync(client, "nobody@example.com", "WrongPass123!");

        var register = await RegisterAsync(client, $"rl-{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    [Fact]
    public async Task JoinCouple_SixthAttemptBySameUser_Returns429_AndOtherUsersAreNotAffected()
    {

        await using var factory = new RateLimitWebApplicationFactory();
        using var attacker = factory.CreateClient();
        using var bystander = factory.CreateClient();

        attacker.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await RegisterAndGetTokenAsync(attacker));
        bystander.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await RegisterAndGetTokenAsync(bystander));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await attacker.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = $"ZZZZZ{attempt}" });
            Assert.True(HttpStatusCode.NotFound == response.StatusCode, $"attempt {attempt}: got {(int)response.StatusCode}");
        }

        var sixth = await attacker.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "ZZZZZ6" });
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        var error = await sixth.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("RATE_LIMIT_EXCEEDED", error!.Code);

        // The budget is per authenticated user, not per IP: another user from the same address still gets through.
        var other = await bystander.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "ZZZZZ7" });
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Refresh_IsNotRateLimited()
    {
        await using var factory = new RateLimitWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { RefreshToken = $"invalid-token-{attempt}" });
            Assert.True(HttpStatusCode.Unauthorized == response.StatusCode, $"attempt {attempt}: got {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Login_LimitIsConfigurable()
    {
        await using var factory = new RateLimitWebApplicationFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Auth:PermitLimit"] = "2"
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "nobody@example.com", "WrongPass123!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "nobody@example.com", "WrongPass123!")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, "nobody@example.com", "WrongPass123!")).StatusCode);
    }

    [Fact]
    public async Task Login_BehindTrustedProxy_IsLimitedPerForwardedClientIp()
    {
        await using var factory = new RateLimitWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = RateLimitWebApplicationFactory.ProxyAddress
        });
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "a@example.com", "WrongPass123!", "203.0.113.10")).StatusCode);

        var sameClient = await LoginAsync(client, "a@example.com", "WrongPass123!", "203.0.113.10");
        var otherClient = await LoginAsync(client, "b@example.com", "WrongPass123!", "203.0.113.20");

        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
    }

    [Fact]
    public async Task Login_FromUntrustedSource_CannotEvadeTheLimitBySpoofingForwardedFor()
    {
        // No proxy configured: X-Forwarded-For sent by the caller must be ignored.
        await using var factory = new RateLimitWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
            await LoginAsync(client, "a@example.com", "WrongPass123!", $"198.51.100.{attempt}");

        var spoofed = await LoginAsync(client, "a@example.com", "WrongPass123!", "198.51.100.99");

        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }

    [Fact]
    public async Task Login_BehindCdn_IsLimitedPerClientIpHeader_EvenWhenForwardedForChanges()
    {
        // PaaS behind a CDN (e.g. Render behind Cloudflare): the proxy appends to X-Forwarded-For,
        // so its last entry is an edge address that changes between requests. The CDN also sets a
        // single-valued header with the real client address; that is the one to partition on.
        await using var factory = new RateLimitWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:TrustAllProxies"] = "true",
            ["ForwardedHeaders:ClientIpHeader"] = "CF-Connecting-IP"
        });
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await LoginWithHeadersAsync(client, "a@example.com",
                ("CF-Connecting-IP", "203.0.113.10"),
                ("X-Forwarded-For", $"203.0.113.10, 172.70.0.{attempt}"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var sameClient = await LoginWithHeadersAsync(client, "a@example.com",
            ("CF-Connecting-IP", "203.0.113.10"),
            ("X-Forwarded-For", "203.0.113.10, 172.70.0.99"));
        var otherClient = await LoginWithHeadersAsync(client, "b@example.com",
            ("CF-Connecting-IP", "203.0.113.20"),
            ("X-Forwarded-For", "203.0.113.20, 172.70.0.99"));

        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
    }

    [Fact]
    public async Task Login_ClientIpHeader_IsIgnoredWhenTheSourceIsNotATrustedProxy()
    {
        // Header configured but no proxy trusted: a caller must not pick its own bucket.
        await using var factory = new RateLimitWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:ClientIpHeader"] = "CF-Connecting-IP"
        });
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
            await LoginWithHeadersAsync(client, "a@example.com", ("CF-Connecting-IP", $"198.51.100.{attempt}"));

        var spoofed = await LoginWithHeadersAsync(client, "a@example.com", ("CF-Connecting-IP", "198.51.100.99"));

        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsTheDeployedVersion_WhenConfigured()
    {
        await using var factory = new RateLimitWebApplicationFactory(new Dictionary<string, string?>
        {
            ["APP_VERSION"] = "abc1234def5678"
        });
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("healthy", payload.GetProperty("status").GetString());
        Assert.Equal("abc1234", payload.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Health_ReportsUnknownVersion_WhenNotConfigured()
    {
        await using var factory = new RateLimitWebApplicationFactory();
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("unknown", payload.GetProperty("version").GetString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> LoginWithHeadersAsync(HttpClient client, string email, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { Email = email, Password = "WrongPass123!" })
        };
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return client.SendAsync(request);
    }


    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { Email = email, Password = password })
        };
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email)
        => client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = "Rate Limit", Password });

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var response = await RegisterAsync(client, $"rl-{Guid.NewGuid():N}@example.com");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("accessToken").GetString()!;
    }

    private sealed record ErrorDto(string Code, string Message, string TraceId);

    private sealed class RateLimitWebApplicationFactory : WebApplicationFactory<Program>
    {
        public const string ProxyAddress = "10.0.0.1";

        private const string JwtSecret = "integration-test-secret-1234567890-abcdef";
        private const string JwtIssuer = "CoupleSync.IntegrationTests";
        private const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

        private readonly string _databaseConnectionString = $"Data Source=couplesync-ratelimit-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
        private SqliteConnection? _keepAliveConnection;

        public RateLimitWebApplicationFactory(IReadOnlyDictionary<string, string?>? extraConfiguration = null)
        {
            _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
            SetJwtEnvironment();
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            SetJwtEnvironment();
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var config = new Dictionary<string, string?>(_extraConfiguration)
                {
                    ["Jwt:Secret"] = JwtSecret,
                    ["Jwt:Issuer"] = JwtIssuer,
                    ["Jwt:Audience"] = JwtAudience
                };
                configBuilder.AddInMemoryCollection(config);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
                _keepAliveConnection.Open();

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_databaseConnectionString));

                // TestServer has no socket, so give every request the address of the "reverse proxy".
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(ProxyAddress)));

                using var scope = services.BuildServiceProvider().CreateScope();
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
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

        private static void SetJwtEnvironment()
        {
            Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
            Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
            Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        }
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        private readonly IPAddress _remoteIp;

        public RemoteIpStartupFilter(IPAddress remoteIp) => _remoteIp = remoteIp;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = _remoteIp;
                    return nextMiddleware();
                });
                next(app);
            };
    }
}
