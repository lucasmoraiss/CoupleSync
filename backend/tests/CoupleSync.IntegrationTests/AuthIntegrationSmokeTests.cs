using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using CoupleSync.Api;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace CoupleSync.IntegrationTests;

public sealed class AuthIntegrationSmokeTests
{
    [Fact]
    public async Task Register_ShouldReturnCreated_AndIssueTokenWithExpectedClaims()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"register-{Guid.NewGuid():N}@example.com";
        var registerResponse = await RegisterAsync(client, email, "User One", "SecurePass123!");

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var payload = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.AccessToken));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(payload.AccessToken);
        var sub = jwt.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Sub).Value;
        var userId = jwt.Claims.Single(x => x.Type == "user_id").Value;
        var coupleId = jwt.Claims.Single(x => x.Type == "couple_id").Value;
        var roles = jwt.Claims.Single(x => x.Type == "roles").Value;

        Assert.Equal(payload.User.Id.ToString(), sub);
        Assert.Equal(payload.User.Id.ToString(), userId);
        Assert.Equal(string.Empty, coupleId);
        Assert.Equal("[]", roles);
    }

    [Fact]
    public async Task Login_ShouldReturnOk_AndContainValidJwt()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"login-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(client, email, "User Two", "SecurePass123!");

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = "SecurePass123!"
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var payload = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.AccessToken));
        Assert.Equal(email, payload.User.Email);

        var principal = new JwtSecurityTokenHandler().ValidateToken(payload.AccessToken, CreateTokenValidationParameters(), out var securityToken);
        Assert.NotNull(principal);

        var jwt = Assert.IsType<JwtSecurityToken>(securityToken);
        Assert.Equal(payload.User.Id.ToString(), jwt.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(payload.User.Id.ToString(), jwt.Claims.Single(x => x.Type == "user_id").Value);
        Assert.Equal(string.Empty, jwt.Claims.Single(x => x.Type == "couple_id").Value);
    }

    [Fact]
    public async Task Refresh_WithValidToken_ShouldReturnOk_WithAccessTokenAndRotatedRefreshToken()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"refresh-happy-{Guid.NewGuid():N}@example.com";
        var registerResponse = await RegisterAsync(client, email, "User Happy", "SecurePass123!");
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        var refreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = auth!.RefreshToken
        });

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var payload = await refreshResponse.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(payload.RefreshToken));
        Assert.NotEqual(auth.RefreshToken, payload.RefreshToken);

        var principal = new JwtSecurityTokenHandler().ValidateToken(payload.AccessToken, CreateTokenValidationParameters(), out var securityToken);
        Assert.NotNull(principal);

        var jwt = Assert.IsType<JwtSecurityToken>(securityToken);
        Assert.Equal(auth.User.Id.ToString(), jwt.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(auth.User.Id.ToString(), jwt.Claims.Single(x => x.Type == "user_id").Value);
        Assert.Equal(string.Empty, jwt.Claims.Single(x => x.Type == "couple_id").Value);
    }

    [Fact]
    public async Task Refresh_ReplayingOldTokenAfterSuccessfulRefresh_ShouldReturnGenericUnauthorizedMessage()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"refresh-replay-{Guid.NewGuid():N}@example.com";
        var registerResponse = await RegisterAsync(client, email, "User Replay", "SecurePass123!");
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        var firstRefreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = auth!.RefreshToken
        });

        Assert.Equal(HttpStatusCode.OK, firstRefreshResponse.StatusCode);

        var firstPayload = await firstRefreshResponse.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.NotNull(firstPayload);
        Assert.False(string.IsNullOrWhiteSpace(firstPayload!.RefreshToken));

        var replayResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = auth.RefreshToken
        });

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        var replayPayload = await replayResponse.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();

        AssertUnauthorizedSession(replayPayload);
    }

    [Fact]
    public async Task Refresh_WithTwoConcurrentRequestsUsingSameToken_ShouldReturnOneSuccessAndOneGenericUnauthorized()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();

        var email = $"refresh-concurrent-{Guid.NewGuid():N}@example.com";
        var registerResponse = await RegisterAsync(clientA, email, "User Concurrent", "SecurePass123!");
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        var firstTask = clientA.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = auth!.RefreshToken
        });

        var secondTask = clientB.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = auth.RefreshToken
        });

        await Task.WhenAll(firstTask, secondTask);

        var responses = new[] { await firstTask, await secondTask };

        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Unauthorized));

        var successResponse = responses.Single(x => x.StatusCode == HttpStatusCode.OK);
        var successPayload = await successResponse.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.NotNull(successPayload);
        Assert.False(string.IsNullOrWhiteSpace(successPayload!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(successPayload.RefreshToken));

        var unauthorizedResponse = responses.Single(x => x.StatusCode == HttpStatusCode.Unauthorized);
        var unauthorizedPayload = await unauthorizedResponse.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        AssertUnauthorizedSession(unauthorizedPayload);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShouldReturnUnauthorizedWithGenericMessage()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"wrong-pass-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(client, email, "User Three", "SecurePass123!");

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = "IncorrectPass999!"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);

        var payload = await loginResponse.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.NotNull(payload);
        Assert.DoesNotContain("wrong password", payload!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid email", payload.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_ShouldReturnGenericUnauthorizedMessage()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"expired-refresh-{Guid.NewGuid():N}@example.com";
        var registerResponse = await RegisterAsync(client, email, "User Four", "SecurePass123!");
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenHasher = scope.ServiceProvider.GetRequiredService<ITokenHasher>();
            var tokenHash = tokenHasher.Hash(auth!.RefreshToken);
            var token = await db.RefreshTokens.SingleAsync(x => x.TokenHash == tokenHash);

            token.Rotate(token.TokenHash, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var refreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = auth!.RefreshToken
        });

        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
        var payload = await refreshResponse.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();

        AssertUnauthorizedSession(payload);
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_ShouldReturnGenericUnauthorizedMessage()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var refreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            RefreshToken = "unknown-refresh-token"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
        var payload = await refreshResponse.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();

        AssertUnauthorizedSession(payload);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ShouldReturnConflict()
    {
        await using var factory = new IntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var email = $"duplicate-{Guid.NewGuid():N}@example.com";
        var first = await RegisterAsync(client, email, "User Five", "SecurePass123!");
        var second = await RegisterAsync(client, email, "User Five Duplicate", "SecurePass123!");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var payload = await second.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.NotNull(payload);
        Assert.Equal("EMAIL_ALREADY_IN_USE", payload!.Code);
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string name, string password)
    {
        return await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = name,
            Password = password
        });
    }

    private static void AssertUnauthorizedSession(ErrorEnvelopeDto? payload)
    {
        Assert.NotNull(payload);
        Assert.Equal("UNAUTHORIZED", payload!.Code);
        Assert.Equal("Invalid or expired session.", payload.Message);
    }

    private static TokenValidationParameters CreateTokenValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(IntegrationWebApplicationFactory.JwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = IntegrationWebApplicationFactory.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = IntegrationWebApplicationFactory.JwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);

    private sealed record RefreshResponseDto(string AccessToken, string? RefreshToken);

    private sealed record AuthUserDto(Guid Id, string Email, string Name);

    private sealed record ErrorEnvelopeDto(string Code, string Message, string TraceId);

    private sealed class IntegrationWebApplicationFactory : WebApplicationFactory<Program>
    {
        public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
        public const string JwtIssuer = "CoupleSync.IntegrationTests";
        public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

        private readonly string _databaseConnectionString = $"Data Source=couplesync-auth-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private SqliteConnection? _keepAliveConnection;

        public IntegrationWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
            Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
            Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
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

                using var scope = services.BuildServiceProvider().CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing)
            {
                return;
            }

            _keepAliveConnection?.Dispose();
            _keepAliveConnection = null;

            Environment.SetEnvironmentVariable("JWT__SECRET", null);
            Environment.SetEnvironmentVariable("JWT__ISSUER", null);
            Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
        }
    }
}
