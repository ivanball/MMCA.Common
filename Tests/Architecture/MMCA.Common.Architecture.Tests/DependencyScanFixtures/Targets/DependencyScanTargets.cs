namespace MMCA.Common.Architecture.Tests.DependencyScanFixtures.Targets;

/// <summary>Reached only from inside a lambda body.</summary>
public static class LambdaTarget
{
    public static int Compute() => 1;
}

/// <summary>Reached only from inside an async method body, after an await.</summary>
public static class AsyncTarget
{
    public static int Compute() => 2;
}

/// <summary>Reached only as an attribute on a type.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AttributeTargetAttribute : Attribute;

/// <summary>Reached only as the element of a rectangular array.</summary>
public sealed class RectangularArrayTarget;

/// <summary>Reached only as the element of a single-dimensional array.</summary>
public sealed class VectorTarget;

/// <summary>Reached only as a by-reference parameter.</summary>
public readonly record struct ByReferenceTarget(int Value);

/// <summary>Declares the nested target.</summary>
public static class TargetHolder
{
    /// <summary>Reached only as a generic argument of an array element.</summary>
    public sealed class NestedTarget;
}

/// <summary>Reached only as the type argument of a generic type.</summary>
public sealed class GenericArgumentTarget;
