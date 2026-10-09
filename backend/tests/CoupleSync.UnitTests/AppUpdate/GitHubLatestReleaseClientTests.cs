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
    // Absolute addresses that HttpClient refuses to send (it would throw NotSupportedException): not http(s).
    [InlineData("github.test:443/repos/example/app/releases/latest")]
    [InlineData("ftp://github.test/repos/example/app/releases/latest")]
    [InlineData("file:///repos/example/app/releases/latest")]
    [InlineData("/repos/example/app/releases/latest")]
    public async Task WithoutAValidAddress_ThereIsNoLookup(string? address)
    {
        Assert.Null(await Client(address).GetLatestTagAsync(CancellationToken.None));

        Assert.Equal(0, _gitHub.Calls);
    }

    [Fact]
    public async Task AFailureAfterAGoodAnswer_KeepsTheLastGoodTag_AndStillAsksAgainAfterAFewMinutes()
    {
        using var client = Client();
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));

        // The hour is over and GitHub now refuses (the 60/h limit is shared by address): the app keeps its notice.
        _clock.UtcNow += TimeSpan.FromMinutes(61);
        _gitHub.Status = HttpStatusCode.Forbidden;
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(2, _gitHub.Calls);

        // The failure is remembered for a few minutes like any other...
        _clock.UtcNow += TimeSpan.FromMinutes(4);
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(2, _gitHub.Calls);

        // ...and then GitHub is asked again; a new good answer replaces the kept one.
        _clock.UtcNow += TimeSpan.FromMinutes(2);
        _gitHub.Status = HttpStatusCode.OK;
        _gitHub.TagName = "v1.2.0";
        Assert.Equal("v1.2.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(3, _gitHub.Calls);
    }

    [Fact]
    public async Task TheLastGoodTag_IsNotKeptForever_AfterADayOfFailuresItIsUnknown()
    {
        using var client = Client();
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        _gitHub.NetworkDown = true;

        _clock.UtcNow += TimeSpan.FromHours(23);
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));

        _clock.UtcNow += TimeSpan.FromHours(2);
        Assert.Null(await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(24), GitHubLatestReleaseClient.LastGoodLifetime);
    }

    [Fact]
    public async Task TheCallerGivingUpInTheMiddleOfTheLookup_IsNotRememberedAsAFailure()
    {
        using var client = Client();
        _gitHub.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var givingUp = new CancellationTokenSource();

        var lookup = client.GetLatestTagAsync(givingUp.Token);
        await WaitUntilAsync(() => _gitHub.Calls == 1);
        await givingUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);

        _gitHub.Hold = null;
        Assert.Equal("v1.1.0", await client.GetLatestTagAsync(CancellationToken.None));
        Assert.Equal(2, _gitHub.Calls);
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
        // GitHub holds the first answer: without "one lookup at a time" every one of the 20 would be calling it by now.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gitHub.Hold = hold;

        var lookups = Enumerable.Range(0, 20).Select(_ => Task.Run(() => client.GetLatestTagAsync(CancellationToken.None))).ToArray();
        await WaitUntilAsync(() => _gitHub.Calls >= 1);
        await Task.Delay(300);
        Assert.Equal(1, _gitHub.Calls);
        Assert.All(lookups, lookup => Assert.False(lookup.IsCompleted));

        hold.SetResult();
        var tags = await Task.WhenAll(lookups);

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
    [InlineData("nightly", "qualquer", null, null)]
    // A minimum above the latest published APK would lock everybody out with no APK to unlock them: ignored.
    [InlineData("v1.1.0", "11.0.0", "1.1.0", null)]
    [InlineData("v1.1.0", "1.1.1", "1.1.0", null)]
    [InlineData("v1.10.0", "1.9.0", "1.10.0", "1.9.0")]
    // Without knowing the latest there is no telling whether such an APK exists: no minimum either.
    [InlineData(null, "1.1.0", null, null)]
    [InlineData("nightly", "1.1.0", null, null)]
    public async Task TheService_NormalizesBothVersions_AndAlwaysGivesTheFixedDownloadLink(
        string? latestTag, string minimum, string? expectedLatest, string? expectedMinimum)
    {
        var service = new AppVersionService(
            new FixedSource(latestTag),
            Options.Create(new AppUpdateOptions { MinimumVersion = minimum }),
            new InvalidMinimumVersionNotice(),
            NullLogger<AppVersionService>.Instance);

        var info = await service.GetAsync(CancellationToken.None);

        Assert.Equal(expectedLatest, info.LatestVersion);
        Assert.Equal(expectedMinimum, info.MinimumVersion);
        Assert.Equal("https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk", info.DownloadUrl);
    }

    [Fact]
    public async Task AMinimumAboveTheLatest_IsLoggedAsAnInvalidConfiguration()
    {
        var logger = new RecordingLogger<AppVersionService>();
        var service = new AppVersionService(
            new FixedSource("v1.1.0"), Options.Create(new AppUpdateOptions { MinimumVersion = "11.0.0" }), new InvalidMinimumVersionNotice(), logger);

        await service.GetAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("APP_MINIMUM_VERSION", entry.Message);
        Assert.Contains("11.0.0", entry.Message);
    }

    [Fact]
    public async Task TheInvalidMinimum_IsLoggedOncePerState_NotOnEveryRequest()
    {
        // The route is asked up to 30 times a minute per address, by a new service each time (it is scoped).
        var logger = new RecordingLogger<AppVersionService>();
        var notice = new InvalidMinimumVersionNotice();
        var source = new FixedSource("v1.1.0");
        AppVersionService Service(string minimum) =>
            new(source, Options.Create(new AppUpdateOptions { MinimumVersion = minimum }), notice, logger);

        for (var request = 0; request < 30; request++) await Service("11.0.0").GetAsync(CancellationToken.None);
        Assert.Single(logger.Entries);

        // Another latest version with the minimum still above it: a new state, said once more.
        source.Tag = "v1.2.0";
        for (var request = 0; request < 30; request++) await Service("11.0.0").GetAsync(CancellationToken.None);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("1.2.0", logger.Entries[1].Message);

        // Valid again (the latest caught up), and nothing is logged; invalid again later, and it is said again.
        source.Tag = "v11.0.0";
        Assert.Equal("11.0.0", (await Service("11.0.0").GetAsync(CancellationToken.None)).MinimumVersion);
        Assert.Equal(2, logger.Entries.Count);
        source.Tag = "v1.2.0";
        await Service("11.0.0").GetAsync(CancellationToken.None);
        await Service("11.0.0").GetAsync(CancellationToken.None);
        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public async Task TheLatestBeingUnknownForAWhile_DoesNotMakeTheSameInvalidMinimumBeLoggedAgain()
    {
        var logger = new RecordingLogger<AppVersionService>();
        var notice = new InvalidMinimumVersionNotice();
        var source = new FixedSource("v1.1.0");
        AppVersionService Service() => new(source, Options.Create(new AppUpdateOptions { MinimumVersion = "11.0.0" }), notice, logger);

        await Service().GetAsync(CancellationToken.None);
        source.Tag = null;
        Assert.Null((await Service().GetAsync(CancellationToken.None)).MinimumVersion);
        source.Tag = "v1.1.0";
        await Service().GetAsync(CancellationToken.None);

        Assert.Single(logger.Entries);
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("v1.1.0", "1.1.0-pit", 0)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void Versions_AreComparedByNumber(string left, string right, int expected)
    {
        Assert.Equal(expected, AppVersionNumber.Compare(left, right));
    }

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", "a mais nova")]
    public void AnUnknownVersion_ComparesAsUnknown(string? left, string? right)
    {
        Assert.Null(AppVersionNumber.Compare(left, right));
    }

    // ---------------------------------------------------------------- support

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in 10 seconds.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FixedSource : ILatestAppReleaseSource
    {
        public FixedSource(string? tag) => Tag = tag;

        public string? Tag { get; set; }

        public Task<string?> GetLatestTagAsync(CancellationToken ct) => Task.FromResult(Tag);
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
