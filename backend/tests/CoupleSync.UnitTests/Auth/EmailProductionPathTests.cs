using System.Net;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Integrations.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Auth;

/// <summary>
/// The real send path - QueuedEmailSender, EmailDispatchService, the typed HttpClient with its logging - against a stub
/// handler. Nothing here reaches api.brevo.com.
/// </summary>
[Trait("Category", "EmailCodes")]
public sealed class EmailProductionPathTests
{
    private const string ApiKey = "xkeysib-prod-path-secret-key";

    private static readonly EmailMessage Message = new("ana@example.com", "", "Assunto", "<p>123456</p>", "Seu código 123456");

    private sealed record LogEntry(string Category, LogLevel Level, string Text);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly CapturingLoggerProvider _owner;

            public CapturingLogger(string category, CapturingLoggerProvider owner)
            {
                _category = category;
                _owner = owner;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + (exception is null ? "" : " " + exception);
                lock (_owner.Entries) _owner.Entries.Add(new LogEntry(_category, logLevel, text));
            }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

        public List<string> Bodies { get; } = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public SemaphoreSlim Handled { get; } = new(0);

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) => _script.Enqueue(step);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            try
            {
                return _script.Count > 0
                    ? await _script.Dequeue()(request, cancellationToken)
                    : new HttpResponseMessage(HttpStatusCode.Created);
            }
            finally
            {
                Handled.Release();
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool configured = true, TimeSpan? httpTimeout = null, int? capacity = null)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = configured ? "brevo" : "",
                ["Email:ApiKey"] = ApiKey,
                ["Email:FromAddress"] = "no-reply@couplesync.app"
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            services.AddEmailSending(config);
            var client = services.AddHttpClient<BrevoEmailClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
            if (httpTimeout is { } timeout)
            {
                client.ConfigureHttpClient(c => c.Timeout = timeout);
            }

            if (capacity is { } cap)
            {
                services.AddSingleton(sp => new QueuedEmailSender(sp.GetRequiredService<IOptions<EmailOptions>>(), sp.GetRequiredService<ILogger<QueuedEmailSender>>(), cap));
            }

            Provider = services.BuildServiceProvider();
            Sender = Provider.GetRequiredService<QueuedEmailSender>();
            Worker = Provider.GetServices<IHostedService>().OfType<EmailDispatchService>().Single();
        }

        public CapturingLoggerProvider Logs { get; } = new();
        public StubHandler Handler { get; } = new();
        public ServiceProvider Provider { get; }
        public QueuedEmailSender Sender { get; }
        public EmailDispatchService Worker { get; }

        public async Task WaitForRequestsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await Handler.Handled.WaitAsync(TimeSpan.FromSeconds(10)), $"request {i + 1} never reached the stub");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            await Provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheRealWorker_DeliversQueuedMessages_ToBrevo()
    {
        await using var rig = new Rig();
        await rig.Worker.StartAsync(CancellationToken.None);

        await rig.Sender.SendAsync(Message, CancellationToken.None);
        await rig.WaitForRequestsAsync(1);

        var request = Assert.Single(rig.Handler.Requests);
        Assert.Equal(BrevoEmailClient.Endpoint, request.RequestUri!.ToString());
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
        var json = JsonDocument.Parse(Assert.Single(rig.Handler.Bodies)).RootElement;
        Assert.Equal("ana@example.com", json.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Contains("123456", json.GetProperty("textContent").GetString());
    }

    [Fact]
    public async Task ANon2xxAnswer_IsLogged_AndTheWorkerKeepsDelivering()
    {
        await using var rig = new Rig();
        rig.Handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("ana@example.com " + ApiKey)
        }));
        await rig.Worker.StartAsync(CancellationToken.None);

        await rig.Sender.SendAsync(Message, CancellationToken.None);
        await rig.Sender.SendAsync(Message with { Subject = "Segundo" }, CancellationToken.None);
        await rig.WaitForRequestsAsync(2);

        Assert.Contains("Segundo", rig.Handler.Bodies[1]);
        var warning = Assert.Single(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(EmailDispatchService)));
        Assert.Contains("500", warning.Text);
        Assert.DoesNotContain(ApiKey, warning.Text);
        Assert.DoesNotContain("ana@example.com", warning.Text);
        Assert.DoesNotContain("123456", warning.Text);
        Assert.False(rig.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task AProviderTimeout_IsLogged_AndTheWorkerKeepsDelivering()
    {
        await using var rig = new Rig(httpTimeout: TimeSpan.FromMilliseconds(150));
        rig.Handler.Enqueue(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        await rig.Worker.StartAsync(CancellationToken.None);

        await rig.Sender.SendAsync(Message, CancellationToken.None);
        await rig.Sender.SendAsync(Message with { Subject = "Segundo" }, CancellationToken.None);
        await rig.WaitForRequestsAsync(2);

        Assert.Contains("Segundo", rig.Handler.Bodies[1]);
        var warning = Assert.Single(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(EmailDispatchService)));
        Assert.Contains("in time", warning.Text);
        Assert.False(rig.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task SendAsync_NeverThrows_AndQueuesNothingWhenNotConfigured()
    {
        await using var rig = new Rig(configured: false);
        await rig.Worker.StartAsync(CancellationToken.None);

        await rig.Sender.SendAsync(Message, CancellationToken.None);
        await Task.Delay(200);

        Assert.False(rig.Sender.IsConfigured);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task WhenTheQueueIsFull_TheDroppedMessageIsLogged()
    {
        await using var rig = new Rig(capacity: 2); // worker not started: nothing drains

        for (var i = 0; i < 3; i++)
        {
            await rig.Sender.SendAsync(Message, CancellationToken.None);
        }

        var warning = Assert.Single(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith(nameof(QueuedEmailSender)));
        Assert.Contains("queue is full", warning.Text);
        Assert.DoesNotContain("123456", warning.Text);
    }

    [Fact]
    public async Task TheApiKeyHeader_IsNeverWrittenToTheHttpClientLogs()
    {
        await using var rig = new Rig();
        await rig.Worker.StartAsync(CancellationToken.None);

        await rig.Sender.SendAsync(Message, CancellationToken.None);
        await rig.WaitForRequestsAsync(1);
        await Task.Delay(100);

        var httpLogs = rig.Logs.Entries.Where(e => e.Category.StartsWith("System.Net.Http.HttpClient")).ToList();
        Assert.NotEmpty(httpLogs); // the pipeline does log at Trace; the key just must not be in it
        Assert.All(httpLogs, e => Assert.DoesNotContain(ApiKey, e.Text));
        Assert.All(rig.Logs.Entries, e => Assert.DoesNotContain(ApiKey, e.Text));
    }
}
