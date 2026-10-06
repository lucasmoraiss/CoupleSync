using CoupleSync.Api.Security;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleSync.IntegrationTests.Security;

/// <summary>
/// A01 / TST-04 — the API must refuse to start without a strong JWT secret supplied
/// from outside the repository, and the versioned appsettings.json must not carry one.
/// </summary>
public sealed class JwtSecretStartupTests
{
    private const string ValidSecret = "startup-test-secret-1234567890-abcdefgh";

    [Fact]
    public void VersionedAppSettings_ShouldNotContainJwtSecret()
    {
        var path = Path.Combine(FindBackendRoot(), "src", "CoupleSync.Api", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.True(
            string.IsNullOrEmpty(configuration["Jwt:Secret"]),
            "appsettings.json is versioned and must not contain a value for Jwt:Secret.");
    }

    [Fact]
    public void Startup_WithoutJwtSecret_ShouldRefuseToStart()
    {
        using var factory = new StartupWebApplicationFactory(secret: null);

        var ex = Record.Exception(() => factory.CreateClient());

        AssertInvalidJwtSecret(ex);
    }

    [Fact]
    public void Startup_WithShortJwtSecret_ShouldRefuseToStart()
    {
        using var factory = new StartupWebApplicationFactory(secret: "short-secret-with-31-characters");

        var ex = Record.Exception(() => factory.CreateClient());

        AssertInvalidJwtSecret(ex);
    }

    [Fact]
    public void Startup_WithPlaceholderJwtSecret_ShouldRefuseToStart()
    {
        using var factory = new StartupWebApplicationFactory(secret: JwtSecretGuard.Placeholder);

        var ex = Record.Exception(() => factory.CreateClient());

        AssertInvalidJwtSecret(ex);
    }

    [Fact]
    public async Task Startup_WithStrongJwtSecret_ShouldStart()
    {
        await using var factory = new StartupWebApplicationFactory(secret: ValidSecret);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("                                        ")]
    [InlineData("short-secret-with-31-characters")]
    [InlineData(JwtSecretGuard.Placeholder)]
    public void Guard_ShouldRejectWeakSecrets(string? secret)
    {
        Assert.False(JwtSecretGuard.IsValid(secret));
        Assert.Throws<InvalidOperationException>(() => JwtSecretGuard.EnsureValid(secret));
    }

    [Fact]
    public void Guard_ShouldAcceptSecretWithAtLeast32Characters()
    {
        Assert.True(JwtSecretGuard.IsValid("exactly-32-characters-secret-abc"));
        JwtSecretGuard.EnsureValid(ValidSecret);
    }

    private static void AssertInvalidJwtSecret(Exception? ex)
    {
        Assert.NotNull(ex);
        Assert.Contains("Invalid JWT secret configuration", ex!.ToString());
    }

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoupleSync.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("CoupleSync.sln not found above the test output directory.");
    }

    /// <summary>
    /// Boots the real Program with only the versioned appsettings.json ("Testing" environment,
    /// so no appsettings.Development.json) and the JWT secret given through JWT__SECRET.
    /// </summary>
    private sealed class StartupWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseConnectionString = $"Data Source=couplesync-startup-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly string? _previousSecret;
        private SqliteConnection? _keepAliveConnection;

        public StartupWebApplicationFactory(string? secret)
        {
            _previousSecret = Environment.GetEnvironmentVariable("JWT__SECRET");
            Environment.SetEnvironmentVariable("JWT__SECRET", secret);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                _keepAliveConnection = new SqliteConnection(_databaseConnectionString);
                _keepAliveConnection.Open();

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_databaseConnectionString));

                using var scope = services.BuildServiceProvider().CreateScope();
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
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
            Environment.SetEnvironmentVariable("JWT__SECRET", _previousSecret);
        }
    }
}
