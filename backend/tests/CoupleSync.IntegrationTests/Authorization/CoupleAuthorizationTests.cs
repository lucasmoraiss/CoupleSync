using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoupleSync.Api.Filters;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.IntegrationTests.Authorization;

public sealed class CoupleAuthorizationTests
{
    [Fact]
    public async Task CoupleScoped_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new AuthorizationWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/test/couple-scoped");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CoupleScoped_AuthenticatedButNoCoupleId_ShouldReturn403WithCoupleRequired()
    {
        await using var factory = new AuthorizationWebApplicationFactory();
        using var client = factory.CreateClient();

        // Register a user — no couple yet, so couple_id claim is empty
        var token = await RegisterAndGetTokenAsync(client, $"no-couple-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/test/couple-scoped");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task CoupleScoped_AuthenticatedAndInCouple_ShouldReturn200()
    {
        await using var factory = new AuthorizationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"in-couple-{Guid.NewGuid():N}@example.com";
        const string password = "SecurePass123!";

        // Register user
        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = password
        });
        registerResponse.EnsureSuccessStatusCode();

        var registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registerAuth!.AccessToken);

        // Create couple (user now has a couple_id in DB, but current token still has empty couple_id)
        var createResponse = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Login again to get a fresh token with couple_id populated
        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginAuth!.AccessToken);

        var response = await client.GetAsync("/api/test/couple-scoped");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

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

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);

    private sealed record ErrorDto(string Code, string Message, string TraceId);

    private sealed class AuthorizationWebApplicationFactory : WebApplicationFactory<Program>
    {
        // Use the same JWT constants as other integration test factories to avoid env-var race
        // conditions when multiple factories run in parallel within the same process.
        public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
        public const string JwtIssuer = "CoupleSync.IntegrationTests";
        public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

        private readonly string _databaseConnectionString = $"Data Source=couplesync-auth-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private SqliteConnection? _keepAliveConnection;

        public AuthorizationWebApplicationFactory()
        {
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

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(_databaseConnectionString);
                });

                // Register test controller from this assembly
                services.AddControllers()
                    .AddApplicationPart(typeof(CoupleAuthorizationTests).Assembly);

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
            // Do NOT null out JWT env vars — nulling races with other parallel test class factories
            // that call SetEnvironmentVariable then immediately read them in CreateHost.
            // The env vars intentionally remain set for the lifetime of the test process.
        }
    }
}

[ApiController]
[Route("api/test")]
[RequireCouple]
public sealed class TestCoupleController : ControllerBase
{
    [HttpGet("couple-scoped")]
    public IActionResult CoupleScoped() => Ok(new { ok = true });
}
