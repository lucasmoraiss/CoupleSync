using System.Net;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Integrations.Email;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Auth;

/// <summary>The Brevo client against a stub handler: nothing here ever reaches api.brevo.com.</summary>
[Trait("Category", "EmailCodes")]
public sealed class BrevoEmailClientTests
{
    private const string ApiKey = "xkeysib-super-secret-test-key";

    private static readonly EmailMessage Message = new("ana@example.com", "Ana", "Assunto", "<p>Olá</p>", "Olá");

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public Func<CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await Respond(cancellationToken);
        }
    }

    private static BrevoEmailClient NewClient(StubHandler handler, TimeSpan? timeout = null) =>
        new(
            new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            Options.Create(new EmailOptions { Provider = "brevo", ApiKey = ApiKey, FromAddress = "no-reply@couplesync.app", FromName = "CoupleSync" }));

    [Fact]
    public async Task Send_PostsToBrevo_WithApiKeyHeader_AndTheDocumentedBody()
    {
        var handler = new StubHandler();

        await NewClient(handler).SendAsync(Message, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", handler.Request.RequestUri!.ToString());
        Assert.Equal(ApiKey, Assert.Single(handler.Request.Headers.GetValues("api-key")));
        Assert.Null(handler.Request.Headers.Authorization);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);

        var json = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("no-reply@couplesync.app", json.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("CoupleSync", json.GetProperty("sender").GetProperty("name").GetString());
        var to = Assert.Single(json.GetProperty("to").EnumerateArray());
        Assert.Equal("ana@example.com", to.GetProperty("email").GetString());
        Assert.Equal("Ana", to.GetProperty("name").GetString());
        Assert.Equal("Assunto", json.GetProperty("subject").GetString());
        Assert.Equal("<p>Olá</p>", json.GetProperty("htmlContent").GetString());
        Assert.Equal("Olá", json.GetProperty("textContent").GetString());
        Assert.DoesNotContain(ApiKey, handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Send_NonSuccess_ThrowsEmailSendException_WithoutLeakingTheKeyOrTheBody(HttpStatusCode status)
    {
        var handler = new StubHandler
        {
            Respond = _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"ana@example.com " + ApiKey + "\"}") })
        };

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => NewClient(handler).SendAsync(Message, CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.DoesNotContain(ApiKey, ex.ToString());
        Assert.DoesNotContain("ana@example.com", ex.ToString());
    }

    [Fact]
    public async Task Send_WhenTheProviderIsTooSlow_TimesOutAsEmailSendException()
    {
        var handler = new StubHandler { Respond = async ct => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); } };

        var ex = await Assert.ThrowsAsync<EmailSendException>(() =>
            NewClient(handler, TimeSpan.FromMilliseconds(100)).SendAsync(Message, CancellationToken.None));

        Assert.Contains("in time", ex.Message);
    }

    [Fact]
    public async Task Send_NetworkFailure_ThrowsEmailSendException()
    {
        var handler = new StubHandler { Respond = _ => throw new HttpRequestException("connection refused: " + ApiKey) };

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => NewClient(handler).SendAsync(Message, CancellationToken.None));

        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task Send_WhenTheCallerCancels_PropagatesCancellation()
    {
        var handler = new StubHandler { Respond = async ct => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); } };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewClient(handler).SendAsync(Message, cts.Token));
    }

    [Theory]
    [InlineData("brevo", "key", "a@b.com", true)]
    [InlineData("BREVO", "key", "a@b.com", true)]
    [InlineData("", "key", "a@b.com", false)]
    [InlineData("brevo", "", "a@b.com", false)]
    [InlineData("brevo", "key", "", false)]
    [InlineData("smtp", "key", "a@b.com", false)]
    public void Options_AreConfiguredOnlyWithProviderKeyAndSender(string provider, string key, string from, bool expected)
    {
        Assert.Equal(expected, new EmailOptions { Provider = provider, ApiKey = key, FromAddress = from }.IsConfigured);
    }
}
