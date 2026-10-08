using System.Net;
using CoupleSync.Application.AppUpdate;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Integrations.GitHub;
using CoupleSync.TestSupport;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.AppUpdate;

/// <summary>
/// Issue #3 — the latest release lookup against <see cref="FakeGitHubReleases"/> (an HttpMessageHandler: no network),
/// with a clock the test moves to cross the cache windows.
/// </summary>
[Trait("Category", "AppUpdate")]
public sealed class GitHubLatestReleaseClientTests
{
    private readonly FakeGitHubReleases _gitHub = new();
    private readonly AdjustableClock _clock = new();
    private readonly MemoryCache _cache;

    public GitHubLatestReleaseClientTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
    }

    private GitHubLatestReleaseClient Client(string? address = FakeGitHubReleases.LatestReleaseUrl) => new(
        new SingleHandlerFactory(_gitHub),
        _cache,
        Options.Create(new AppUpdateOptions { LatestReleaseUrl = address! }),
        NullLogger<GitHubLatestReleaseClient>.Instance);

    [Fact]
    public async Task ReturnsTheTagOfTheLatestRelease_AskingWithAUserAgentAndWithoutCredentials()
    {
        _gitHub.TagName = "v1.2.0";

        var tag = await Client().GetLatestTagAsync(CancellationToken.None);

        Assert.Equal("v1.2.0", tag);
        var request = Assert.Single(_gitHub.Requests);
        Assert.Equal(("GET", FakeGitHubReleases.LatestReleaseUrl), (request.Method, request.Url));
        Assert.Equal(GitHubLatestReleaseClient.UserAgent, request.UserAgent);
        Assert.False(request.HasAuthorization);
    }

    [Fact]
    public async Task AGoodAnswer_IsReusedForAnHour_ThenAskedAgain()
    {
        using var client = Client();

        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        _gitHub.TagName = "v1.2.0";
        _clock.UtcNow += TimeSpan.FromMinutes(59);
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(1, _gitHub.Calls);

        _clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Equal("v1.2.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(2, _gitHub.Calls);
    }

    [Fact]
    public async Task AFailure_IsRememberedForAFewMinutes_ThenAskedAgain()
    {
        using var client = Client();
        _gitHub.Status = HttpStatusCode.InternalServerError;

        Assert.Null(await client.GetLatestTagAsync(CancellationToken.None));
        _gitHub.Status = HttpStatusCode.OK;
        _clock.UtcNow += TimeSpan.FromMinutes(4);
        Assert.Null(await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(1, _gitHub.Calls);

        _clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(2, _gitHub.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task AnAnswerThatIsNotSuccess_IsNull_NotAnException(HttpStatusCode status)
    {
        _gitHub.Status = status;

        Assert.Null(await Client().GetLatestTagAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>manutenção</html>")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"v1.1.0\"")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"name\":\"sem tag\"}")]
    [InlineData("{\"tag_name\":\"v1.1.0\"")]
    public async Task AnUnexpectedBody_IsNull_NotAnException(string body)
    {
        _gitHub.Body = body;

        Assert.Null(await Client().GetLatestTagAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NetworkDownOrTimeout_IsNull_NotAnException()
    {
        _gitHub.NetworkDown = true;
        Assert.Null(await Client().GetLatestTagAsync(CancellationToken.None));

        _cache.Clear();
        _gitHub.NetworkDown = false;
        _gitHub.TimesOut = true;
        Assert.Null(await Client().GetLatestTagAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("isto não é um endereço")]
    public async Task WithoutAValidAddress_ThereIsNoLookup(string? address)
    {
        Assert.Null(await Client(address).GetLatestTagAsync(CancellationToken.None));

        Assert.Equal(0, _gitHub.Calls);
    }

    [Fact]
    public async Task TheCallerGivingUp_IsNotRememberedAsAFailure()
    {
        using var client = Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestTagAsync(cancelled.Token));

        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestsThatArriveTogether_ShareOneLookup()
    {
        using var client = Client();

        var tags = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => client.GetLatestTagAsync(CancellationToken.None))));

        Assert.All(tags, tag => Assert.Equal("v1.1.0", tag));
        Assert.Equal(1, _gitHub.Calls);
    }

    [Fact]
    public void TheTimeoutIsShort_AndTheWindowsAreTheOnesOfTheDecision()
    {
        Assert.InRange(GitHubLatestReleaseClient.RequestTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromHours(1), GitHubLatestReleaseClient.SuccessLifetime);
        Assert.InRange(GitHubLatestReleaseClient.FailureLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    }

    // ---------------------------------------------------------------- the service on top of it

    [Theory]
    [InlineData("v1.1.0", "", "1.1.0", null)]
    [InlineData("v1.2.0", "1.1.0", "1.2.0", "1.1.0")]
    [InlineData("v1.0.0-pit", "v1.0.0", "1.0.0", "1.0.0")]
    [InlineData(null, "1.1.0", null, "1.1.0")]
    [InlineData("nightly", "qualquer", null, null)]
    public async Task TheService_NormalizesBothVersions_AndAlwaysGivesTheFixedDownloadLink(
        string? latestTag, string minimum, string? expectedLatest, string? expectedMinimum)
    {
        var service = new AppVersionService(new FixedSource(latestTag), Options.Create(new AppUpdateOptions { MinimumVersion = minimum }));

        var info = await service.GetAsync(CancellationToken.None);

        Assert.Equal(expectedLatest, info.LatestVersion);
        Assert.Equal(expectedMinimum, info.MinimumVersion);
        Assert.Equal("https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk", info.DownloadUrl);
    }

    // ---------------------------------------------------------------- support

    private sealed class FixedSource : ILatestAppReleaseSource
    {
        private readonly string? _tag;

        public FixedSource(string? tag) => _tag = tag;

        public Task<string?> GetLatestTagAsync(CancellationToken ct) => Task.FromResult(_tag);
    }

    private sealed class AdjustableClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SingleHandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(GitHubLatestReleaseClient.HttpClientName, name);
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
