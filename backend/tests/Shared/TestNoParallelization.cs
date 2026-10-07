using System.Reflection;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoupleSync.TestSupport;

/// <summary>
/// Test hosts read process-wide state (JWT__SECRET, DATABASE_URL, ...) and the factories set and clear it. Two test
/// classes running at the same time race on it (an Unauthorized in the middle of a flow, for instance). This file is
/// compiled into every test project that links the shared host code, so such a project can never run in parallel.
/// </summary>
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
