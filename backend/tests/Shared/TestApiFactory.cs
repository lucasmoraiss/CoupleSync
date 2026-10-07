using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoupleSync.TestSupport;

/// <summary>
/// The single point every API test host goes through (issue #16). It is compiled into every test project that starts
/// the API; <c>TestHostGuardTests</c> fails when a <see cref="WebApplicationFactory{TEntryPoint}"/> class skips it and
/// <c>TestHostSourceGuardTests</c> fails on any host built without a class and on any test project that does not link it.
/// <list type="bullet">
/// <item>The host always runs in the "Testing" environment, so appsettings.Development.json (which on the owner's
/// machine points at production) is never loaded. Applied in <see cref="CreateHost"/>, which is sealed and runs after
/// every <c>ConfigureWebHost</c>, so a factory cannot move itself out of "Testing" nor skip this.</item>
/// <item>DATABASE_URL, which outranks configuration in <c>DatabaseConnectionResolver</c>, is set to
/// <see cref="DatabaseConnectionString"/> before the host is built: the machine's value is never used. By default
/// that is <see cref="TestDatabaseIsolation.UnreachableConnectionString"/>, which no test can connect to; hosts that
/// need a real database (PostgreSQL in a Testcontainers container) override it, and hosts on SQLite replace the
/// DbContext.</item>
/// <item>The assembly attribute above is declared here, not in a file of its own: the host code reads and writes
/// process-wide state (JWT__SECRET, DATABASE_URL, ...), two test classes running at the same time race on it, and a
/// project that links this file can therefore never run in parallel.</item>
/// </list>
/// A factory that needs configuration declares it itself (in-memory collection or environment variable), and process
/// state that must be in place right before the host is built goes in <see cref="BeforeCreateHost"/>.
/// </summary>
internal class TestApiFactory : WebApplicationFactory<Program>
{
    internal const string TestEnvironmentName = "Testing";

    protected virtual string DatabaseConnectionString => TestDatabaseIsolation.UnreachableConnectionString;

    /// <summary>Hook for process-wide state (environment variables) a factory needs right before the host is built.</summary>
    protected virtual void BeforeCreateHost()
    {
    }

    protected sealed override IHost CreateHost(IHostBuilder builder)
    {
        BeforeCreateHost();
        builder.UseEnvironment(TestEnvironmentName);

        var connectionString = DatabaseConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // An empty value would clear DATABASE_URL and hand the decision back to configuration.
            throw new InvalidOperationException($"{GetType().Name}.{nameof(DatabaseConnectionString)} must not be empty.");
        }

        Environment.SetEnvironmentVariable("DATABASE_URL", connectionString);
        return base.CreateHost(builder);
    }

    // Other ways in which a derived factory could build the host on its own terms: closed, like CreateHost.
    protected sealed override IWebHostBuilder? CreateWebHostBuilder() => base.CreateWebHostBuilder();

    protected sealed override TestServer CreateServer(IWebHostBuilder builder) => base.CreateServer(builder);

    /// <summary>
    /// A host derived from this one with extra configuration (<c>WithWebHostBuilder</c>), without the test having to name
    /// <c>WebApplicationFactory</c> (the source guard forbids the name outside the shared files).
    /// </summary>
    internal DerivedTestHost WithTestHostBuilder(Action<IWebHostBuilder> configure) => new(WithWebHostBuilder(configure));
}

/// <summary>What the tests that derive a host need from it: clients, services and disposal.</summary>
internal sealed class DerivedTestHost : IDisposable, IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _host;

    internal DerivedTestHost(WebApplicationFactory<Program> host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public HttpClient CreateClient() => _host.CreateClient();

    public void Dispose() => _host.Dispose();

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

public sealed class TestNoParallelizationTests
{
    [Fact]
    public void ATestProjectThatStartsHosts_DoesNotRunItsTestsInParallel()
    {
        var behavior = typeof(TestApiFactory).Assembly.GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.NotNull(behavior);
        Assert.True(behavior!.DisableTestParallelization, "DisableTestParallelization must be true.");
    }
}
