using MMCA.Common.Architecture.Tests.DependencyScanFixtures.Targets;

namespace MMCA.Common.Architecture.Tests.DependencyScanFixtures;

/// <summary>
/// Compiled subjects for <c>ArchitectureTypesTests</c>. Each one names a type from
/// <see cref="Targets"/> in exactly one place, and only in a place a dependency scan that reads
/// signatures alone would miss, so a regression in body scanning fails a test instead of letting a
/// layer rule pass vacuously.
/// </summary>
public static class LambdaOnlyDependent
{
    /// <summary>The target is named inside the lambda, which compiles to a method of a nested closure type.</summary>
    public static Func<int> Make() => () => LambdaTarget.Compute();
}

/// <summary>The target is named after the await, which compiles into a nested state machine's MoveNext.</summary>
public static class AsyncOnlyDependent
{
    public static async Task<int> RunAsync()
    {
        await Task.Yield();
        return AsyncTarget.Compute();
    }
}

/// <summary>The target is the type's only attribute.</summary>
[AttributeTarget]
public sealed class AttributeOnlyDependent;

/// <summary>The target is only ever the type argument of a collection.</summary>
public sealed class GenericArgumentOnlyDependent
{
    public IReadOnlyList<GenericArgumentTarget> Items { get; init; } = [];
}

/// <summary>Names nothing from <see cref="Targets"/>: the control.</summary>
public static class IndependentSubject
{
    public static Func<int> Make() => () => 3;
}
