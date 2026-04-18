using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.IntegrationTests;

public sealed class CoupleIntegrationSmokeTests
{
    [Fact]
    public async Task CreateCouple_WithoutAuth_ShouldReturnUnauthorized()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/couples", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateCouple_AuthenticatedUser_ShouldReturnCreatedWithJoinCode()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"create-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/couples", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CreateCoupleDto>();
        Assert.NotNull(payload);
        Assert.NotEqual(Guid.Empty, payload!.CoupleId);
        Assert.Equal(6, payload.JoinCode.Length);
    }

    [Fact]
    public async Task CreateCouple_WhenUserAlreadyInCouple_ShouldReturnConflict()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"already-in-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var payload = await second.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("USER_ALREADY_IN_COUPLE", payload!.Code);
    }

    [Fact]
    public async Task JoinCouple_WithoutAuth_ShouldReturnUnauthorized()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "ABC123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCoupleMe_WithoutAuth_ShouldReturnUnauthorized()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/couples/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JoinCouple_WithValidJoinCode_ShouldReturnOkWithBothMembers()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();

        var tokenA = await RegisterAndGetTokenAsync(clientA, $"joiner-a-{Guid.NewGuid():N}@example.com");
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var createResponse = await clientA.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCoupleDto>();
        Assert.NotNull(created);

        var tokenB = await RegisterAndGetTokenAsync(clientB, $"joiner-b-{Guid.NewGuid():N}@example.com");
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var joinResponse = await clientB.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = created!.JoinCode });
        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);

        var joinPayload = await joinResponse.Content.ReadFromJsonAsync<JoinCoupleDto>();
        Assert.NotNull(joinPayload);
        Assert.Equal(2, joinPayload!.Members.Count);
    }

    [Fact]
    public async Task JoinCouple_WhenCoupleFull_ShouldReturnConflict()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        using var clientC = factory.CreateClient();

        var tokenA = await RegisterAndGetTokenAsync(clientA, $"full-a-{Guid.NewGuid():N}@example.com");
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var createResponse = await clientA.PostAsJsonAsync("/api/v1/couples", new { });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCoupleDto>();
        Assert.NotNull(created);

        var tokenB = await RegisterAndGetTokenAsync(clientB, $"full-b-{Guid.NewGuid():N}@example.com");
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var joinB = await clientB.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = created!.JoinCode });
        Assert.Equal(HttpStatusCode.OK, joinB.StatusCode);

        var tokenC = await RegisterAndGetTokenAsync(clientC, $"full-c-{Guid.NewGuid():N}@example.com");
        clientC.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenC);
        var joinC = await clientC.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = created.JoinCode });
        Assert.Equal(HttpStatusCode.Conflict, joinC.StatusCode);

        var payload = await joinC.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_FULL", payload!.Code);
    }

    [Fact]
    public async Task JoinCouple_WithInvalidJoinCode_ShouldReturnNotFound()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"no-code-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = "XXXXXX" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_NOT_FOUND", payload!.Code);
    }

    [Fact]
    public async Task GetCoupleMe_WhenNotInCouple_ShouldReturnNotFound()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterAndGetTokenAsync(client, $"me-alone-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/couples/me");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(payload);
        Assert.Equal("COUPLE_NOT_FOUND", payload!.Code);
    }

    [Fact]
    public async Task GetCoupleMe_WhenInCouple_ShouldReturnCoupleDetails()
    {
        await using var factory = new CoupleIntegrationWebApplicationFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();

        var tokenA = await RegisterAndGetTokenAsync(clientA, $"me-a-{Guid.NewGuid():N}@example.com");
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var createResponse = await clientA.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCoupleDto>();
        Assert.NotNull(created);

        var tokenB = await RegisterAndGetTokenAsync(clientB, $"me-b-{Guid.NewGuid():N}@example.com");
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var joinResponse = await clientB.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = created!.JoinCode });
        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);

        var meResponse = await clientA.GetAsync("/api/v1/couples/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var mePayload = await meResponse.Content.ReadFromJsonAsync<GetCoupleMeDto>();
        Assert.NotNull(mePayload);
        Assert.NotEqual(Guid.Empty, mePayload!.CoupleId);
        Assert.Equal(6, mePayload.JoinCode.Length);
        Assert.Equal(2, mePayload.Members.Count);
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

    private sealed record CreateCoupleDto(Guid CoupleId, string JoinCode);

    private sealed record JoinCoupleDto(Guid CoupleId, IReadOnlyCollection<CoupleMemberDto> Members);

    private sealed record GetCoupleMeDto(Guid CoupleId, string JoinCode, DateTime CreatedAtUtc, IReadOnlyCollection<CoupleMemberDto> Members);

    private sealed record CoupleMemberDto(Guid UserId, string Name, string Email);

    private sealed record ErrorDto(string Code, string Message, string TraceId);

    private sealed class CoupleIntegrationWebApplicationFactory : WebApplicationFactory<Program>
    {
        public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
        public const string JwtIssuer = "CoupleSync.IntegrationTests";
        public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

        private readonly string _databaseConnectionString = $"Data Source=couplesync-couple-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private SqliteConnection? _keepAliveConnection;

        public CoupleIntegrationWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
            Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
            Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Re-apply env vars right before construction to guard against parallel test teardown races
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
            Environment.SetEnvironmentVariable("JWT__SECRET", null);
            Environment.SetEnvironmentVariable("JWT__ISSUER", null);
            Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
        }
    }
}
