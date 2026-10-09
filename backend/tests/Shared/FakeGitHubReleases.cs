using System.Net;
using System.Text;

namespace CoupleSync.TestSupport;

/// <summary>
/// "Latest release" of the GitHub API as far as the tests need it, in memory: no test ever talks to api.github.com.
/// </summary>
internal sealed class FakeGitHubReleases : HttpMessageHandler
{
    public const string LatestReleaseUrl = "https://github.test/repos/example/app/releases/latest";

    private readonly object _gate = new();
    private readonly List<RecordedGitHubRequest> _requests = new();

    /// <summary>Tag of the latest release (GitHub's <c>tag_name</c>).</summary>
    public string TagName { get; set; } = "v1.1.0";

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>When set, the body of the answer whatever the tag (to test unexpected shapes).</summary>
    public string? Body { get; set; }

    /// <summary>Every request fails before any answer (DNS, connection refused).</summary>
    public bool NetworkDown { get; set; }

    /// <summary>Every request times out (what HttpClient raises when its Timeout elapses).</summary>
    public bool TimesOut { get; set; }

    /// <summary>
    /// When set, every request is recorded and then waits here (GitHub being slow) until the test completes it;
    /// the wait ends early if the caller gives up.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    public IReadOnlyList<RecordedGitHubRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public int Calls
    {
        get { lock (_gate) return _requests.Count; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(new RecordedGitHubRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.UserAgent.ToString(),
                request.Headers.Authorization is not null));
        }

        if (!string.Equals(request.RequestUri!.ToString(), LatestReleaseUrl, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fake GitHub was called with another address: {request.RequestUri}.");

        if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);

        if (NetworkDown) throw new HttpRequestException("Connection refused (fake).");
        if (TimesOut) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout (fake).", new TimeoutException());

        var body = Body ?? "{\"url\":\"https://github.test/releases/1\",\"tag_name\":\"" + TagName + "\",\"draft\":false,\"prerelease\":false}";
        return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal sealed record RecordedGitHubRequest(string Method, string Url, string UserAgent, bool HasAuthorization);
