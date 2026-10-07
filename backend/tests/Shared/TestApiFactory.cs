using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.TestSupport;

/// <summary>
/// The single point every API test host goes through (issue #16). It is compiled into every test project that starts
/// the API, and <c>TestHostGuardTests</c> fails when a <see cref="WebApplicationFactory{TEntryPoint}"/> skips it.
/// <list type="bullet">
/// <item>The host always runs in the "Testing" environment, so appsettings.Development.json (which on the owner's
/// machine points at production) is never loaded. Applied in <see cref="CreateHost"/>, after every
/// <c>ConfigureWebHost</c>, so a factory cannot move itself out of "Testing".</item>
/// <item>DATABASE_URL, which outranks configuration in <c>DatabaseConnectionResolver</c>, is set to
/// <see cref="DatabaseConnectionString"/> before the host is built: the machine's value is never used. By default
/// that is <see cref="UnreachableConnectionString"/>, which no test can connect to; hosts that need a real database
/// (PostgreSQL in a Testcontainers container) override it, and hosts on SQLite replace the DbContext.</item>
/// </list>
/// A factory that needs configuration declares it itself (in-memory collection or environment variable).
/// </summary>
internal class TestApiFactory : WebApplicationFactory<Program>
{
    internal const string TestEnvironmentName = "Testing";

    /// <summary>Harmless value: nothing listens on port 1 and the credentials are not real.</summary>
    internal const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=couplesync_unused_in_tests;Username=test;Password=test;Timeout=1";

    protected virtual string DatabaseConnectionString => UnreachableConnectionString;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironmentName);
        Environment.SetEnvironmentVariable("DATABASE_URL", DatabaseConnectionString);
        return base.CreateHost(builder);
    }
}
