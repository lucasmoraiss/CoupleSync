using CoupleSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.TestSupport;

/// <summary>
/// Compiled into the test projects that run the API on SQLite. Behaviour of the shared test host (issue #16):
/// "Testing" environment, no appsettings.Development.json, no dependency on the machine's DATABASE_URL.
/// </summary>
public sealed class TestHostEnvironmentTests
{
    [Fact]
    public void ASharedBaseHost_RunsInTheTestingEnvironment()
    {
        using var factory = new SqliteApiFactory();

        Assert.Equal("Testing", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public void AHostCreatedWithWithWebHostBuilder_AlsoRunsInTheTestingEnvironment()
    {
        using var factory = new SqliteApiFactory();
        using var derived = factory.WithWebHostBuilder(builder => builder.ConfigureServices(_ => { }));

        Assert.Equal("Testing", derived.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public void ATestHost_CannotBeMovedOutOfTesting_ByItsOwnConfiguration()
    {
        using var factory = new SqliteApiFactory(builder => builder.UseEnvironment("Development"));

        Assert.Equal("Testing", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public void ATestHost_DoesNotLoadAnAppsettingsDevelopmentFile()
    {
        var contentRoot = Directory.CreateTempSubdirectory("couplesync-testing-env-").FullName;
        try
        {
            // Harmless sentinel, never a real connection string.
            File.WriteAllText(
                Path.Combine(contentRoot, "appsettings.Development.json"),
                "{ \"TestSentinel\": \"development-file-was-loaded\" }");

            using var factory = new SqliteApiFactory(builder => builder.UseContentRoot(contentRoot));

            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            Assert.Null(configuration["TestSentinel"]);
            Assert.Equal(contentRoot, factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void ATestHost_IgnoresADatabaseUrlOfTheMachine_UnlessTheFactoryDeclaresItsOwn()
    {
        var previous = Environment.GetEnvironmentVariable("DATABASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "Host=machine-database.invalid;Database=must-not-be-used;Username=x;Password=x");

            using var factory = new SqliteApiFactory();
            _ = factory.Services;

            var resolved = DatabaseConnectionResolver.Resolve(factory.Services.GetRequiredService<IConfiguration>());
            Assert.Equal(DatabaseConnectionResolver.ParseDatabaseUrl(TestApiFactory.UnreachableConnectionString), resolved);
            Assert.DoesNotContain("machine-database", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", previous);
        }
    }

    /// <summary>The API on an in-memory SQLite database, with only what the host needs to start.</summary>
    private sealed class SqliteApiFactory : TestApiFactory
    {
        private readonly string _connectionString = $"Data Source=couplesync-host-guard-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly Action<IWebHostBuilder>? _configure;
        private SqliteConnection? _keepAlive;

        public SqliteApiFactory(Action<IWebHostBuilder>? configure = null)
        {
            _configure = configure;
            Environment.SetEnvironmentVariable("JWT__SECRET", "host-guard-test-secret-1234567890-abcdef");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _configure?.Invoke(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                _keepAlive = new SqliteConnection(_connectionString);
                _keepAlive.Open();

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));

                using var scope = services.BuildServiceProvider().CreateScope();
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _keepAlive?.Dispose();
            }
        }
    }
}
