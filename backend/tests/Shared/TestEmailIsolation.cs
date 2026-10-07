using System.Runtime.CompilerServices;

namespace CoupleSync.TestSupport;

/// <summary>
/// Compiled into every test project that can reach the API (all five, see TestHostSourceGuardTests). Runs when the test assembly loads, before any
/// host is built, and turns e-mail sending off for the whole process: environment variables outrank appsettings*.json, so
/// even a machine that has Email__Provider/ApiKey/FromAddress set (real Brevo credentials) never makes a test host send.
/// A new WebApplicationFactory gets this for free. Tests that need a configured sender register their own fake IEmailSender.
/// </summary>
internal static class TestEmailIsolation
{
    internal const string DisabledProvider = "disabled-in-tests";

    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("Email__Provider", DisabledProvider);
    }
}

public sealed class TestEmailIsolationTests
{
    [Fact]
    public void TheTestProcess_NeverCarriesARealEmailProvider()
    {
        Assert.Equal(TestEmailIsolation.DisabledProvider, Environment.GetEnvironmentVariable("Email__Provider"));
    }
}
