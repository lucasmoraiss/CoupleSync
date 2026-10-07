using System.Reflection;
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
        var types = typeof(TestApiFactory).Assembly.GetTypes()
            // The deliberately unprotected example of the guard's own test is the only exception.
            .Where(t => t.DeclaringType != typeof(TestHostGuardTests))
            .ToList();

        // A scan that finds no factory at all must fail instead of passing: this assembly has at least one protected host.
        Assert.Contains(types, t => t.IsClass && t != typeof(TestApiFactory) && typeof(TestApiFactory).IsAssignableFrom(t));

        var unprotected = TestApiFactoryGuard.FindUnprotected(types);

        Assert.True(
            unprotected.Count == 0,
            "These test hosts do not derive from CoupleSync.TestSupport.TestApiFactory (the \"Testing\" environment point): "
            + string.Join(", ", unprotected.Select(t => t.FullName)));
    }

    /// <summary>A derived factory cannot skip the base's CreateHost (environment, DATABASE_URL); it uses BeforeCreateHost.</summary>
    [Fact]
    public void TheSharedBase_CannotHaveItsCreateHostReplaced()
    {
        var createHost = typeof(TestApiFactory).GetMethod(
            "CreateHost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.NotNull(createHost);
        Assert.True(createHost!.IsFinal, "TestApiFactory.CreateHost must be sealed.");
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
