using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoupleSync.IntegrationTests.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.IntegrationTests.ApiErrors;

/// <summary>
/// Every 4xx/5xx of every route uses one body: <c>{ code, message, errors?, traceId }</c> with a stable
/// UPPER_SNAKE_CASE code and a Brazilian Portuguese message. One test per class of error.
/// </summary>
[Trait("Category", "ApiErrors")]
public sealed class ApiErrorFormatTests
{
    private const string Password = "SecurePass123!";
    private static readonly Regex CodePattern = new("^[A-Z]+(_[A-Z]+)*$");

    // ── validation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FluentValidationFailure_Returns400WithFieldErrorsInPortuguese()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/v1/auth/register",
            """{"email":"nao-e-email","name":"","password":"123"}""");

        var body = await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Email", out var email) && email.GetArrayLength() > 0);
        Assert.True(errors.TryGetProperty("Password", out var password) && password.GetArrayLength() > 0);
        Assert.True(errors.TryGetProperty("Name", out var name) && name.GetArrayLength() > 0);
        foreach (var field in errors.EnumerateObject())
            foreach (var message in field.Value.EnumerateArray())
                AssertPortuguese(message.GetString()!);
    }

    [Fact]
    public async Task EmptyJsonObject_ReportsEveryMissingFieldInPortuguese()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/v1/auth/register", "{}");

        var body = await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        var errors = body.GetProperty("errors");
        Assert.True(errors.EnumerateObject().Any());
        foreach (var field in errors.EnumerateObject())
            foreach (var message in field.Value.EnumerateArray())
                AssertPortuguese(message.GetString()!);
    }

    [Fact]
    public async Task CustomValidationRule_Returns400WithPortugueseMessage()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            """{"amount":10,"currency":"REAISREAIS","category":"Compras"}""");

        var body = await AssertErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(body.GetProperty("errors").TryGetProperty("Currency", out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400InvalidRequestBody()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/v1/auth/login", """{"email": "a@b.com", "password": """);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST_BODY");
    }

    [Fact]
    public async Task WrongFieldType_Returns400InvalidRequestBody()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await PostJsonAsync(client, "/api/v1/transactions",
            """{"amount":"abc","currency":"BRL","category":"Compras"}""");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST_BODY");
    }

    [Fact]
    public async Task MissingBody_Returns400InPortuguese()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/login",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ControllerLevelBadRequest_UsesItsOwnStableCode()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await client.GetAsync("/api/v1/cashflow?horizon=7");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_HORIZON");
    }

    // ── 401 / 403 / 404 / 405 / 409 ────────────────────────────────────────

    [Fact]
    public async Task MissingToken_Returns401Unauthorized()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/transactions");

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task WrongCredentials_Returns401WithDomainCode()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Email = "ninguem@example.com", Password });

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task UserWithoutCouple_Returns403CoupleRequired()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await RegisterAsync(client, $"solo-{Guid.NewGuid():N}@example.com");

        var response = await client.GetAsync("/api/v1/transactions");

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "COUPLE_REQUIRED");
    }

    [Fact]
    public async Task TransactionOfAnotherGroup_Returns404WithDomainCode()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var created = await PostJsonAsync(client, "/api/v1/transactions",
            """{"amount":10,"currency":"BRL","category":"Compras"}""");
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Another group's token: answered as "not found", never leaking that the transaction exists.
        using var other = factory.CreateClient();
        await AuthenticateWithCoupleAsync(other);
        var response = await other.DeleteAsync($"/api/v1/transactions/{id}");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "TRANSACTION_NOT_FOUND");
    }

    [Fact]
    public async Task UnknownResource_Returns404WithDomainCode()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        await AuthenticateWithCoupleAsync(client);

        var response = await client.GetAsync($"/api/v1/goals/{Guid.NewGuid()}");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "GOAL_NOT_FOUND");
    }

    [Fact]
    public async Task UnknownRoute_Returns404NotFound()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/nao-existe");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task WrongHttpMethod_Returns405MethodNotAllowed()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/login");

        await AssertErrorAsync(response, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
    }

    [Fact]
    public async Task DuplicateEmail_Returns409WithDomainCode()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(client, email);

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = email, Name = "Outra Pessoa", Password });

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "EMAIL_ALREADY_IN_USE");
    }

    // ── 500 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnhandledException_Returns500WithNeutralMessageAndNoInternals()
    {
        await using var factory = new TransactionWebApplicationFactory();
        using var client = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ApiErrorFormatTests).Assembly))).CreateClient();

        var response = await client.GetAsync("/api/test/api-errors/boom");

        var body = await AssertErrorAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_SERVER_ERROR");
        var raw = body.GetRawText();
        Assert.DoesNotContain("SegredoInterno", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("   at ", raw);
        Assert.DoesNotContain("CoupleSync.", raw);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var properties = body.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Subset(new HashSet<string> { "code", "message", "errors", "traceId" }, properties);
        Assert.Contains("code", properties);
        Assert.Contains("message", properties);
        Assert.Contains("traceId", properties);

        var actualCode = body.GetProperty("code").GetString()!;
        Assert.Matches(CodePattern, actualCode);
        if (code is not null) Assert.Equal(code, actualCode);

        AssertPortuguese(body.GetProperty("message").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        return body;
    }

    /// <summary>Cheap guard against the English framework/domain texts the API used to leak.</summary>
    private static void AssertPortuguese(string message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        string[] english = [" must ", "is required", "is invalid", "not found", "Invalid", "Unauthorized", "Exception", " field ", "The ", "already", "unexpected", "Please"];
        foreach (var word in english)
            Assert.DoesNotContain(word, message, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
        => client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<string> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = email, Name = "Test User", Password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = auth.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    private static async Task AuthenticateWithCoupleAsync(HttpClient client)
    {
        var email = $"err-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(client, email);

        var couple = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, couple.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loggedIn.GetProperty("accessToken").GetString());
    }
}

[ApiController]
[Route("api/test/api-errors")]
public sealed class ApiErrorsTestController : ControllerBase
{
    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("SegredoInterno: falha em CoupleSync.Infrastructure.Foo");
}
