using System.Reflection;

namespace CoupleSync.UnitTests.Support;

/// <summary>
/// A dependency the code under test must not touch in this scenario: any call fails the test, naming the member.
/// </summary>
public class NeverCalled<T> : DispatchProxy where T : class
{
    public static T Create() => Create<T, NeverCalled<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} was not expected to be called.");
}
