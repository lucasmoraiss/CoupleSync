using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleSync.IntegrationTests.AppUpdate;

/// <summary>
/// Issue #3 — <c>GET /api/v1/app/version</c>: what the installed app asks to know whether there is a newer APK.
/// GitHub is <see cref="FakeGitHubReleases"/> (no network).
/// </summary>
[Trait("Category", "AppUpdate")]
public sealed class AppVersionEndpointTests
{
    private const string Route = "/api/v1/app/version";
    private const string FixedDownloadUrl = "https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk";

    // ---------------------------------------------------------------- B1

    [Fact]
    public async Task WithoutToken_WithoutConfiguration_AndWithGitHubDown_Answers200WithTheThreeFields_VersionsNull()
    {
        await using var factory = new AppVersionApiFactory();
        factory.GitHub.NetworkDown = true;
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "downloadUrl", "latestVersion", "minimumVersion" },
            payload.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("minimumVersion").ValueKind);
        Assert.Equal(FixedDownloadUrl, payload.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task WithATokenThatNoLongerIsValid_StillAnswers200_TheAppAlwaysSendsItsToken()
    {
        await using var factory = new AppVersionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "expired.or.garbage");

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.1.0", payload.GetProperty("latestVersion").GetString());
    }

    [Fact]
    public async Task WithoutTheLookupAddress_NeverCallsGitHub_AndAnswersNull()
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?> { ["AppUpdate:LatestReleaseUrl"] = "" });
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
        Assert.Equal(0, factory.GitHub.Calls);
    }

    [Fact]
    public async Task TheLatestVersion_IsTheTagOfTheLatestRelease_WithoutPrefixNorSuffix()
    {
        await using var factory = new AppVersionApiFactory();
        factory.GitHub.TagName = "v1.2.0-pit";
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal("1.2.0", payload.GetProperty("latestVersion").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("minimumVersion").ValueKind);
        var request = Assert.Single(factory.GitHub.Requests);
        Assert.Equal(("GET", FakeGitHubReleases.LatestReleaseUrl), (request.Method, request.Url));
        Assert.False(string.IsNullOrWhiteSpace(request.UserAgent));
        Assert.False(request.HasAuthorization);
    }

    [Theory]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData(" v1.0.0 ", "1.0.0")]
    [InlineData("", null)]
    [InlineData("a mais nova", null)]
    [InlineData("1.1", null)]
    // Above the latest published version (1.1.0): it would lock everybody out, so it is ignored.
    [InlineData("11.0.0", null)]
    public async Task TheMinimumVersion_ComesFromTheEnvironmentVariable_InvalidOrAbsentIsNull(string configured, string? expected)
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?> { ["APP_MINIMUM_VERSION"] = configured });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, payload.GetProperty("minimumVersion").GetString());
        Assert.Equal("1.1.0", payload.GetProperty("latestVersion").GetString());
    }

    [Fact]
    public async Task WithGitHubDown_TheMinimumVersionIsNotAnswered_NobodyIsBlockedWithoutAKnownLatestVersion()
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?> { ["APP_MINIMUM_VERSION"] = "1.1.0" });
        factory.GitHub.NetworkDown = true;
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(Route);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("minimumVersion").ValueKind);
    }

    [Theory]
    [InlineData("github.test:443/repos/example/app/releases/latest")]
    [InlineData("ftp://github.test/repos/example/app/releases/latest")]
    public async Task ALookupAddressThatIsNotHttp_Answers200WithNull_Never500(string address)
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?> { ["AppUpdate:LatestReleaseUrl"] = address });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
        Assert.Equal(0, factory.GitHub.Calls);
    }

    // ---------------------------------------------------------------- B2

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.Forbidden, null)]
    [InlineData(HttpStatusCode.OK, "<html>manutenção</html>")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":42}")]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"nightly\"}")]
    [InlineData(HttpStatusCode.OK, "{\"name\":\"sem tag\"}")]
    public async Task GitHubFailingOrAnsweringSomethingUnexpected_Answers200WithLatestVersionNull(HttpStatusCode status, string? body)
    {
        await using var factory = new AppVersionApiFactory();
        factory.GitHub.Status = status;
        factory.GitHub.Body = body;
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
        Assert.Equal(FixedDownloadUrl, payload.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task GitHubTimingOut_Answers200WithLatestVersionNull()
    {
        await using var factory = new AppVersionApiFactory();
        factory.GitHub.TimesOut = true;
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
    }

    [Fact]
    public async Task AGoodAnswer_IsReusedInsideTheCacheWindow_GitHubIsCalledOnce()
    {
        await using var factory = new AppVersionApiFactory();
        using var client = factory.CreateClient();

        for (var request = 1; request <= 5; request++)
        {
            var payload = await client.GetFromJsonAsync<JsonElement>(Route);
            Assert.Equal("1.1.0", payload.GetProperty("latestVersion").GetString());
        }

        Assert.Equal(1, factory.GitHub.Calls);
    }

    [Fact]
    public async Task AFailure_IsAlsoRemembered_GitHubIsNotCalledAgainRightAway()
    {
        await using var factory = new AppVersionApiFactory();
        factory.GitHub.Status = HttpStatusCode.InternalServerError;
        using var client = factory.CreateClient();

        for (var request = 1; request <= 5; request++)
        {
            var payload = await client.GetFromJsonAsync<JsonElement>(Route);
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("latestVersion").ValueKind);
        }

        Assert.Equal(1, factory.GitHub.Calls);
    }

    // ---------------------------------------------------------------- B11

    [Fact]
    public async Task TheRouteIsOnlyAdded_HealthAndTheProtectedRoutesAnswerAsBefore()
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?> { ["APP_VERSION"] = "abc1234def5678" });
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal(new[] { "status", "timestamp", "version" },
            health.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.Equal("abc1234", health.GetProperty("version").GetString());

        // The new route is the only anonymous one under /api/v1/app; the routes the installed app uses still ask for the token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(0, factory.GitHub.Calls);
    }

    // ---------------------------------------------------------------- B12

    [Fact]
    public async Task TheRouteIsLimitedPerClientIp_TheNextRequestGets429InTheStandardEnvelope()
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = AppVersionApiFactory.ProxyAddress,
        });
        using var client = factory.CreateClient();

        // Default budget: 30 per minute per client IP.
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var response = await GetFromAsync(client, "203.0.113.10");
            Assert.True(HttpStatusCode.OK == response.StatusCode, $"attempt {attempt}: got {(int)response.StatusCode}");
        }

        var rejected = await GetFromAsync(client, "203.0.113.10");
        var otherClient = await GetFromAsync(client, "203.0.113.20");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RATE_LIMIT_EXCEEDED", error.GetProperty("code").GetString());
        Assert.Equal("Muitas tentativas. Aguarde um instante e tente novamente.", error.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task TheLimit_IsConfigurable_AndDoesNotSpendTheLoginBudget()
    {
        await using var factory = new AppVersionApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:AppVersion:PermitLimit"] = "2",
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(Route)).StatusCode);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = "nobody@example.com", Password = "WrongPass123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private static Task<HttpResponseMessage> GetFromAsync(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    /// <summary>The API on SQLite with the named HTTP client "GitHubReleases" pointed at <see cref="FakeGitHubReleases"/>.</summary>
    private sealed class AppVersionApiFactory : TestApiFactory
    {
        public const string ProxyAddress = "10.0.0.1";

        private const string JwtSecret = "integration-test-secret-1234567890-abcdef";
        private const string JwtIssuer = "CoupleSync.IntegrationTests";
        private const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

        private readonly string _databaseConnectionString = $"Data Source=couplesync-appversion-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
        private SqliteConnection? _keepAliveConnection;

        public AppVersionApiFactory(IReadOnlyDictionary<string, string?>? extraConfiguration = null)
        {
            _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
            SetJwtEnvironment();
        }

        public FakeGitHubReleases GitHub { get; } = new();

        protected override void BeforeCreateHost() => SetJwtEnvironment();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var config = new Dictionary<string, string?>
                {
                    ["Jwt:Secret"] = JwtSecret,
                    ["Jwt:Issuer"] = JwtIssuer,
                    ["Jwt:Audience"] = JwtAudience,
                    ["AppUpdate:LatestReleaseUrl"] = FakeGitHubReleases.LatestReleaseUrl,
                    // Empty (not absent) so that a value in the machine's environment can never leak into a test.
                    ["APP_MINIMUM_VERSION"] = string.Empty,
                };
                foreach (var (key, value) in _extraConfiguration) config[key] = value;
                configBuilder.AddInMemoryCollection(config);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
                _keepAliveConnection.Open();

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_databaseConnectionString));

                // Every call of the named client "GitHubReleases" lands on the in-memory fake: no network.
                services.AddHttpClient("GitHubReleases").ConfigurePrimaryHttpMessageHandler(() => GitHub);

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
