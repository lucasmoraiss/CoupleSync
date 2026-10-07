using Microsoft.AspNetCore.Mvc.Testing;

namespace CoupleSync.TestSupport;

/// <summary>
/// Compiled into every test project that starts the API. Guards issue #16: no test host may ever read
/// appsettings.Development.json (on the owner's machine it points at production) or depend on DATABASE_URL.
/// </summary>
public sealed class TestHostGuardTests
{
    private sealed class UnprotectedFactoryExample : WebApplicationFactory<Program>
    {
    }

    private sealed class ProtectedFactoryExample : TestApiFactory
    {
    }

    /// <summary>A new factory that skips the shared base class makes this fail.</summary>
    [Fact]
    public void EveryWebApplicationFactoryInThisAssembly_GoesThroughTheSharedBase()
    {
        var unprotected = TestApiFactoryGuard.FindUnprotected(typeof(TestApiFactory).Assembly.GetTypes()
            // The deliberately unprotected example of the guard's own test is the only exception.
            .Where(t => t.DeclaringType != typeof(TestHostGuardTests)));

        Assert.True(
            unprotected.Count == 0,
            "These test hosts do not derive from CoupleSync.TestSupport.TestApiFactory (the \"Testing\" environment point): "
            + string.Join(", ", unprotected.Select(t => t.FullName)));
    }

    [Fact]
    public void TheGuard_FlagsAFactoryWithoutTheSharedBase_AndAcceptsOneWithIt()
    {
        var found = TestApiFactoryGuard.FindUnprotected(new[]
        {
            typeof(UnprotectedFactoryExample),
            typeof(ProtectedFactoryExample),
            typeof(TestApiFactory),
            typeof(string),
        });

        Assert.Equal(new[] { typeof(UnprotectedFactoryExample) }, found);
    }
}

internal static class TestApiFactoryGuard
{
    /// <summary>
    /// Types that are a WebApplicationFactory of anything but do not derive from <see cref="TestApiFactory"/>
    /// (the single point that forces the "Testing" environment).
    /// </summary>
    internal static IReadOnlyList<Type> FindUnprotected(IEnumerable<Type> types)
    {
        return types
            .Where(t => t.IsClass && t != typeof(TestApiFactory))
            .Where(DerivesFromWebApplicationFactory)
            .Where(t => !typeof(TestApiFactory).IsAssignableFrom(t))
            .ToList();
    }

    private static bool DerivesFromWebApplicationFactory(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(WebApplicationFactory<>))
            {
                return true;
            }
        }

        return false;
    }
}
