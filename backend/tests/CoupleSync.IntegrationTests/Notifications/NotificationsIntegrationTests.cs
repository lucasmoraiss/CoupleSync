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

namespace CoupleSync.IntegrationTests.Notifications;

public sealed class NotificationsIntegrationTests
{
    // ── POST /api/v1/devices/token ─────────────────────────────────────────

    [Fact]
    public async Task RegisterDeviceToken_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/devices/token",
            new { Token = "fake-fcm-token", Platform = "android" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDeviceToken_WithValidToken_ShouldReturn204()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"reg-token-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/devices/token",
            new { Token = "valid-fcm-token-abc123", Platform = "android" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDeviceToken_WithEmptyToken_ShouldReturn400()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"reg-empty-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/devices/token",
            new { Token = "", Platform = "android" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDeviceToken_WithUnsupportedPlatform_ShouldReturn400()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"reg-ios-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/devices/token",
            new { Token = "ios-token-xyz", Platform = "ios" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDeviceToken_RegisterTwice_IsIdempotentReturns204()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"reg-upsert-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsJsonAsync("/api/v1/devices/token",
            new { Token = "fcm-token-v1", Platform = "android" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/devices/token",
            new { Token = "fcm-token-v2", Platform = "android" });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    // ── GET /api/v1/notifications/settings ────────────────────────────────

    [Fact]
    public async Task GetNotificationSettings_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/notifications/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetNotificationSettings_NewUser_ShouldReturn200WithDefaults()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"get-settings-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/notifications/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<NotificationSettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.True(payload!.LowBalanceEnabled);
        Assert.True(payload.LargeTransactionEnabled);
        Assert.True(payload.BillReminderEnabled);
    }

    // ── PUT /api/v1/notifications/settings ────────────────────────────────

    [Fact]
    public async Task UpdateNotificationSettings_WithoutAuth_ShouldReturn401()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/v1/notifications/settings",
            new { LowBalanceEnabled = false });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateNotificationSettings_DisableLowBalance_ShouldReturn204AndPersist()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        var token = await RegisterWithCoupleAndGetTokenAsync(client, $"upd-settings-{Guid.NewGuid():N}@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var update = await client.PutAsJsonAsync("/api/v1/notifications/settings",
            new { LowBalanceEnabled = (bool?)false, LargeTransactionEnabled = (bool?)null, BillReminderEnabled = (bool?)null });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var getResp = await client.GetAsync("/api/v1/notifications/settings");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var payload = await getResp.Content.ReadFromJsonAsync<NotificationSettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.False(payload!.LowBalanceEnabled);
        Assert.True(payload.LargeTransactionEnabled);
    }

    [Fact]
    public async Task GetNotificationSettings_CrossCoupleIsolation_CoupleBCannotSeeCoupleASettings()
    {
        await using var factory = new NotificationsWebApplicationFactory();
        using var client = factory.CreateClient();

        // Couple A disables one setting
        var emailA = $"iso-a-{Guid.NewGuid():N}@example.com";
        var tokenA = await RegisterWithCoupleAndGetTokenAsync(client, emailA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        await client.PutAsJsonAsync("/api/v1/notifications/settings",
            new { LowBalanceEnabled = (bool?)false, LargeTransactionEnabled = (bool?)null, BillReminderEnabled = (bool?)null });

        // Couple B's settings should still be defaults (all enabled)
        var emailB = $"iso-b-{Guid.NewGuid():N}@example.com";
        var tokenB = await RegisterWithCoupleAndGetTokenAsync(client, emailB);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var respB = await client.GetAsync("/api/v1/notifications/settings");
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var payloadB = await respB.Content.ReadFromJsonAsync<NotificationSettingsResponseDto>();
        Assert.NotNull(payloadB);
        // Couple B's settings are independent — should see defaults, not couple A's disabled state
        Assert.True(payloadB!.LowBalanceEnabled);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<string> RegisterWithCoupleAndGetTokenAsync(HttpClient client, string email)
    {
        const string password = "SecurePass123!";

        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = "Test User",
            Password = password
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registerAuth!.AccessToken);

        var createCoupleResponse = await client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, createCoupleResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = email,
            Password = password
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return loginAuth!.AccessToken;
    }

    // ── Local DTOs ─────────────────────────────────────────────────────────

    private sealed record NotificationSettingsResponseDto(
        Guid UserId,
        bool LowBalanceEnabled,
        bool LargeTransactionEnabled,
        bool BillReminderEnabled,
        DateTime UpdatedAtUtc);

    private sealed record AuthResponseDto(AuthUserDto User, string AccessToken, string RefreshToken);
    private sealed record AuthUserDto(Guid Id, string Email, string Name);
}

internal sealed class NotificationsWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.IntegrationTests";
    public const string JwtAudience = "CoupleSync.Mobile.IntegrationTests";

    private readonly string _databaseConnectionString =
        $"Data Source=couplesync-notifications-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _keepAliveConnection;

    public NotificationsWebApplicationFactory()
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
