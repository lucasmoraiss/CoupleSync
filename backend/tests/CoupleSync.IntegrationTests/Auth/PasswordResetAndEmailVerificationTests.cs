using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.IntegrationTests.Transactions;
using Microsoft.AspNetCore.Hosting;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleSync.IntegrationTests.Auth;

/// <summary>Password reset and e-mail confirmation over HTTP. A fake sender stands in for Brevo: no real e-mail is ever sent.</summary>
[Trait("Category", "EmailCodes")]
public sealed class PasswordResetAndEmailVerificationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string StrongPassword = "SecurePass123!";

    private sealed class FakeEmailSender : IEmailSender
    {
        public bool IsConfigured { get; set; } = true;

        public Exception? Failure { get; set; }

        public List<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }

        public string LastCodeFor(string address) =>
            Regex.Match(Sent.Last(m => m.ToAddress == address).TextContent, @"\b\d{6}\b").Value;
    }

    private static DerivedTestHost WithSender(TransactionWebApplicationFactory factory, FakeEmailSender sender) =>
        factory.WithTestHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The route budget (5/min per IP) is covered by RateLimitingIntegrationTests; these tests need more room.
                ["RateLimiting:Auth:PermitLimit"] = "100"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
            });
        });

    private sealed record Registered(string Email, Guid UserId, string AccessToken, string RefreshToken, JsonElement Body);

    private static async Task<Registered> RegisterAsync(HttpClient client, string label = "user")
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { Email = email, Name = label, Password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new Registered(
            email,
            body.GetProperty("user").GetProperty("id").GetGuid(),
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            body);
    }

    private static HttpClient Authorized(TestApiFactory factory, string accessToken) =>
        WithBearer(factory.CreateClient(), accessToken);

    private static HttpClient Authorized(DerivedTestHost factory, string accessToken) =>
        WithBearer(factory.CreateClient(), accessToken);

    private static HttpClient WithBearer(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    private static Task<HttpResponseMessage> Forgot(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { Email = email });

    private static Task<HttpResponseMessage> Reset(HttpClient client, string email, string code, string newPassword = "NovaSenha456") =>
        client.PostAsJsonAsync("/api/v1/auth/reset-password", new { Email = email, Code = code, NewPassword = newPassword });

    // test hosts never reach the real provider

    [Fact]
    public async Task TestHosts_NeverHaveEmailConfigured_EvenWhenTheMachineHasEmailSettings()
    {
        // Run with Email__Provider=brevo, Email__ApiKey and Email__FromAddress exported: a test host must still be "off".
        await using var factory = new TransactionWebApplicationFactory();

        Assert.False(factory.Services.GetRequiredService<IEmailSender>().IsConfigured);
    }

    // not configured

    [Fact]
    public async Task WithoutEmailConfigured_TheRoutesAnswer503_AndTheApiKeepsWorking()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        var user = await RegisterAsync(anonymous); // registration must not depend on e-mail

        var forgot = await Forgot(anonymous, user.Email);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, forgot.StatusCode);
        Assert.Equal("EMAIL_NOT_CONFIGURED", (await BodyOf(forgot)).GetProperty("code").GetString());

        var reset = await Reset(anonymous, user.Email, "123456");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reset.StatusCode);
        Assert.Equal("EMAIL_NOT_CONFIGURED", (await BodyOf(reset)).GetProperty("code").GetString());

        using var signedIn = Authorized(factory, user.AccessToken);
        var confirm = await signedIn.PostAsJsonAsync("/api/v1/auth/confirm-email", new { Code = "123456" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, confirm.StatusCode);
        var resend = await signedIn.PostAsync("/api/v1/auth/resend-email-verification", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resend.StatusCode);
        Assert.Equal("EMAIL_NOT_CONFIGURED", (await BodyOf(resend)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, Password = StrongPassword })).StatusCode);
    }

    [Fact]
    public async Task ExistingAccounts_AreUnverified_ButLoginAndEverythingElseWorksAsBefore()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        var user = await RegisterAsync(anonymous);

        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, Password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await BodyOf(login)).GetProperty("user").GetProperty("emailVerified").GetBoolean());

        using var signedIn = Authorized(factory, user.AccessToken);
        var me = await signedIn.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False((await BodyOf(me)).GetProperty("emailVerified").GetBoolean());
        // No group yet: 404 COUPLE_NOT_FOUND, exactly as for a verified account (not 401/403).
        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/api/v1/couples/me")).StatusCode);
    }

    // password reset

    [Fact]
    public async Task ForgotPassword_AnswersTheSame_ForKnownAndUnknownAddresses_AndSendsOnlyToTheKnownOne()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var user = await RegisterAsync(client);
        sender.Sent.Clear();

        var known = await Forgot(client, user.Email);
        var unknown = await Forgot(client, $"ghost-{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        var message = Assert.Single(sender.Sent);
        Assert.Equal(user.Email, message.ToAddress);
    }

    [Fact]
    public async Task ForgotPassword_WhenTheProviderFails_AnswersTheSameAnyway()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var user = await RegisterAsync(client);
        sender.Failure = new HttpRequestException("provider down");

        var known = await Forgot(client, user.Email);
        var unknown = await Forgot(client, $"ghost-{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Registration_Succeeds_WhenTheProviderFails()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender { Failure = new HttpRequestException("provider down") };
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();

        var user = await RegisterAsync(client);

        Assert.False(string.IsNullOrEmpty(user.AccessToken));
    }

    [Fact]
    public async Task ResetPassword_WithTheEmailedCode_ChangesThePassword_AndEndsEverySession()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var user = await RegisterAsync(client);
        await Forgot(client, user.Email);
        var code = sender.LastCodeFor(user.Email);

        var reset = await Reset(client, user.Email, code);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { user.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, Password = StrongPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, Password = "NovaSenha456" })).StatusCode);

        var reuse = await Reset(client, user.Email, code, "OutraSenha789");
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal("INVALID_CODE", (await BodyOf(reuse)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResetPassword_AfterFiveWrongCodes_TheRightOneNoLongerWorks()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var user = await RegisterAsync(client);
        await Forgot(client, user.Email);
        var right = sender.LastCodeFor(user.Email);
        var wrong = right == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var attempt = await Reset(client, user.Email, wrong);
            Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);
            Assert.Equal("INVALID_CODE", (await BodyOf(attempt)).GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(client, user.Email, right)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, Password = StrongPassword })).StatusCode);
    }

    [Fact]
    public async Task ResetPassword_UnknownAddress_AnswersLikeAWrongCode()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var user = await RegisterAsync(client);
        await Forgot(client, user.Email);

        var wrongCode = await Reset(client, user.Email, "000000");
        var unknown = await Reset(client, $"ghost-{Guid.NewGuid():N}@example.com", "000000");

        Assert.Equal(wrongCode.StatusCode, unknown.StatusCode);
        var a = await BodyOf(wrongCode);
        var b = await BodyOf(unknown);
        Assert.Equal(a.GetProperty("code").GetString(), b.GetProperty("code").GetString());
        Assert.Equal(a.GetProperty("message").GetString(), b.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("curta1", "A senha precisa ter pelo menos 8 caracteres.")]
    [InlineData("somenteletras", "A senha precisa ter pelo menos um número.")]
    [InlineData("senha123", "Essa senha é muito comum. Escolha outra.")]
    public async Task ResetPassword_AppliesThePasswordRule(string newPassword, string expected)
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var user = await RegisterAsync(client);
        await Forgot(client, user.Email);
        var code = sender.LastCodeFor(user.Email);

        var response = await Reset(client, user.Email, code, newPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var messages = (await BodyOf(response)).GetProperty("errors").GetProperty("NewPassword").EnumerateArray().Select(m => m.GetString());
        Assert.Contains(expected, messages);
        // the code was not burned
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(client, user.Email, code)).StatusCode);
    }

    [Fact]
    public async Task ResetPassword_BadlyFormedCode_IsAValidationError()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();

        var response = await Reset(client, "ana@example.com", "12ab");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // e-mail confirmation

    [Fact]
    public async Task EmailConfirmation_FullFlow_FromSignUpToVerified()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var sender = new FakeEmailSender();
        await using var factory = WithSender(baseFactory, sender);
        using var anonymous = factory.CreateClient();
        var user = await RegisterAsync(anonymous);
        Assert.False(user.Body.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        using var signedIn = Authorized(factory, user.AccessToken);

        var wrong = sender.LastCodeFor(user.Email) == "000000" ? "000001" : "000000";
        var bad = await signedIn.PostAsJsonAsync("/api/v1/auth/confirm-email", new { Code = wrong });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("INVALID_CODE", (await BodyOf(bad)).GetProperty("code").GetString());
        Assert.False((await BodyOf(await signedIn.GetAsync("/api/v1/auth/me"))).GetProperty("emailVerified").GetBoolean());

        var resend = await signedIn.PostAsync("/api/v1/auth/resend-email-verification", null);
        Assert.Equal(HttpStatusCode.NoContent, resend.StatusCode);
        var code = sender.LastCodeFor(user.Email);

        var ok = await signedIn.PostAsJsonAsync("/api/v1/auth/confirm-email", new { Code = code });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.True((await BodyOf(await signedIn.GetAsync("/api/v1/auth/me"))).GetProperty("emailVerified").GetBoolean());

        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, Password = StrongPassword });
        Assert.True((await BodyOf(login)).GetProperty("user").GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task ConfirmEmail_RequiresASession()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        await using var factory = WithSender(baseFactory, new FakeEmailSender());
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/confirm-email", new { Code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/auth/resend-email-verification", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // the production send path (real queue + worker + Brevo client) against a stub handler: still no real e-mail

    private sealed class BrevoStub : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        public List<string> Bodies { get; } = new();

        public SemaphoreSlim Handled { get; } = new(0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.ToString());
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies) Bodies.Add(body);
            Handled.Release();
            return new HttpResponseMessage(Status);
        }

        public string LastCode() => Regex.Match(JsonDocument.Parse(Bodies.Last()).RootElement.GetProperty("textContent").GetString()!, @"\b\d{6}\b").Value;
    }

    private static DerivedTestHost WithBrevoStub(TransactionWebApplicationFactory factory, BrevoStub stub) =>
        factory.WithTestHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "brevo",
                ["Email:ApiKey"] = "stub-key",
                ["Email:FromAddress"] = "no-reply@couplesync.app",
                ["RateLimiting:Auth:PermitLimit"] = "100"
            }));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<CoupleSync.Infrastructure.Integrations.Email.BrevoEmailClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => stub));
        });

    [Fact]
    public async Task ProductionSendPath_DeliversTheCodeThroughTheQueueAndTheBrevoClient()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var stub = new BrevoStub();
        await using var factory = WithBrevoStub(baseFactory, stub);
        using var client = factory.CreateClient();

        var user = await RegisterAsync(client);
        Assert.True(await stub.Handled.WaitAsync(TimeSpan.FromSeconds(10)), "the sign-up code never reached the Brevo client");
        await Forgot(client, user.Email);
        Assert.True(await stub.Handled.WaitAsync(TimeSpan.FromSeconds(10)), "the reset code never reached the Brevo client");

        var reset = await Reset(client, user.Email, stub.LastCode());
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    [Fact]
    public async Task ProductionSendPath_WhenBrevoFails_RequestsStillSucceed_AndLaterSendsStillWork()
    {
        await using var baseFactory = new TransactionWebApplicationFactory();
        var stub = new BrevoStub { Status = HttpStatusCode.InternalServerError };
        await using var factory = WithBrevoStub(baseFactory, stub);
        using var client = factory.CreateClient();

        var user = await RegisterAsync(client); // 201 although the provider answers 500
        Assert.True(await stub.Handled.WaitAsync(TimeSpan.FromSeconds(10)));
        var forgot = await Forgot(client, user.Email);
        Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);
        Assert.True(await stub.Handled.WaitAsync(TimeSpan.FromSeconds(10)));

        stub.Status = HttpStatusCode.Created; // the worker survived the failures
        await Forgot(client, user.Email);
        Assert.True(await stub.Handled.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(client, user.Email, stub.LastCode())).StatusCode);
    }
}
