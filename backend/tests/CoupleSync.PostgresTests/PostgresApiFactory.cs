using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.PostgresTests;

/// <summary>
/// The real API on a real PostgreSQL database of the test container. The host runs in the "Testing" environment
/// (so appsettings.Development.json is never loaded), its connection string is forced to the container's and checked
/// by <see cref="PostgresServer.Guard"/> before any context is built. Program.cs applies every migration from zero at
/// startup, exactly as in production.
/// </summary>
internal sealed class PostgresApiFactory : TestApiFactory
{
    public const string JwtSecret = "postgres-test-secret-1234567890-abcdef";
    public const string JwtIssuer = "CoupleSync.PostgresTests";
    public const string JwtAudience = "CoupleSync.Mobile.PostgresTests";

    private readonly TestDatabase _database;

    public PostgresApiFactory(TestDatabase database)
    {
        _database = database;
        _database.Server.Guard(database.ConnectionString);
        SetEnvironment();
    }

    private void SetEnvironment()
    {
        Environment.SetEnvironmentVariable("JWT__SECRET", JwtSecret);
        Environment.SetEnvironmentVariable("JWT__ISSUER", JwtIssuer);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", JwtAudience);
        // Whatever resolves a connection string from the environment finds the container, never another database.
        Environment.SetEnvironmentVariable("DATABASE_URL", _database.ConnectionString);
    }

    protected override string DatabaseConnectionString => _database.ConnectionString;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        SetEnvironment();
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["ConnectionStrings:DefaultConnection"] = _database.ConnectionString,
                ["RateLimiting:Auth:PermitLimit"] = "10000",
                ["RateLimiting:CoupleJoin:PermitLimit"] = "10000",
                ["RateLimiting:Refresh:PermitLimit"] = "10000",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options =>
            {
                _database.Server.Guard(_database.ConnectionString);
                options.UseNpgsql(_database.ConnectionString);
            });

            services.RemoveAll<IStorageAdapter>();
            services.AddScoped<IStorageAdapter, FakeStorageAdapter>();
            services.RemoveAll<IOcrProvider>();
            services.AddScoped<IOcrProvider, FakeOcrProvider>();

            var ocrBackgroundJob = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OcrBackgroundJob));
            if (ocrBackgroundJob is not null) services.Remove(ocrBackgroundJob);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Environment.SetEnvironmentVariable("JWT__SECRET", null);
        Environment.SetEnvironmentVariable("JWT__ISSUER", null);
        Environment.SetEnvironmentVariable("JWT__AUDIENCE", null);
        Environment.SetEnvironmentVariable("DATABASE_URL", null);
    }

    /// <summary>A new DbContext (no group filter) on the same database, for seeding and for assertions.</summary>
    public AppDbContext NewContext()
    {
        _database.Server.Guard(_database.ConnectionString);
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_database.ConnectionString).Options);
    }

    /// <summary>Registers a user and, when asked, gives it its own group. The client carries the resulting token.</summary>
    public async Task<TestUser> RegisterAsync(string name, bool createGroup = true, string? joinCode = null)
    {
        var client = CreateClient();
        var email = $"pg-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Email = email,
            Name = name,
            Password = "SecurePass123!"
        });
        register.EnsureSuccessStatusCode();
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        var userId = registered.GetProperty("user").GetProperty("id").GetGuid();
        var refreshToken = registered.GetProperty("refreshToken").GetString()!;
        Use(client, registered.GetProperty("accessToken").GetString()!);

        Guid? coupleId = null;
        string? code = joinCode;
        if (joinCode is not null)
        {
            var join = await client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
            join.EnsureSuccessStatusCode();
            var joined = await join.Content.ReadFromJsonAsync<JsonElement>();
            Use(client, joined.GetProperty("accessToken").GetString()!);
            coupleId = joined.GetProperty("coupleId").GetGuid();
        }
        else if (createGroup)
        {
            var created = await client.PostAsJsonAsync("/api/v1/couples", new { });
            created.EnsureSuccessStatusCode();
            var couple = await created.Content.ReadFromJsonAsync<JsonElement>();
            Use(client, couple.GetProperty("accessToken").GetString()!);
            coupleId = couple.GetProperty("coupleId").GetGuid();
            code = couple.GetProperty("joinCode").GetString();
        }

        return new TestUser(client, userId, coupleId, code, email, refreshToken);
    }

    private static void Use(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

internal sealed record TestUser(HttpClient Client, Guid UserId, Guid? CoupleId, string? JoinCode, string Email, string RefreshToken);

internal sealed class FakeStorageAdapter : IStorageAdapter
{
    public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
        => Task.FromResult($"fake/couples/{coupleId}/{uploadId}");

    public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
        => Task.FromResult<Stream>(new MemoryStream());

    public Task DeleteAsync(string storagePath, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeOcrProvider : IOcrProvider
{
    public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
        => Task.FromResult("""{"analyzeResult":{"documents":[]}}""");
}
