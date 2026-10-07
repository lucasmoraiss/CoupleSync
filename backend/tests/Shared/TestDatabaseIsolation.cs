using System.Runtime.CompilerServices;

namespace CoupleSync.TestSupport;

/// <summary>
/// Per-process safety belt (issue #16), compiled into EVERY test project that can reach the API (the source guard
/// <c>TestHostSourceGuardTests</c> fails when one does not link it). Runs when the test assembly loads, before any test
/// and before any host exists, and overwrites DATABASE_URL with <see cref="UnreachableConnectionString"/>.
/// DATABASE_URL outranks configuration in <c>DatabaseConnectionResolver</c>, so even a host that escapes the shared
/// <c>TestApiFactory</c> (a bare <c>new WebApplicationFactory&lt;Program&gt;()</c>, <c>WebApplication.CreateBuilder</c>, a
/// factory in a project nobody remembered to protect) cannot resolve a real connection string: not the machine's value,
/// and not the one in appsettings.Development.json. Hosts that need a database of their own (the PostgreSQL container)
/// declare it through <c>TestApiFactory.DatabaseConnectionString</c> and put this value back when they are disposed.
/// </summary>
internal static class TestDatabaseIsolation
{
    /// <summary>Harmless value: nothing listens on port 1 and the credentials are not real.</summary>
    internal const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=couplesync_unused_in_tests;Username=test;Password=test;Timeout=1";

    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("DATABASE_URL", UnreachableConnectionString);
    }
}

public sealed class TestDatabaseIsolationTests
{
    [Fact]
    public void TheTestProcess_NeverCarriesARealDatabaseUrl()
    {
        Assert.Equal(TestDatabaseIsolation.UnreachableConnectionString, Environment.GetEnvironmentVariable("DATABASE_URL"));
    }
}
