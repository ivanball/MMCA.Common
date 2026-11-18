using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// The "no type in this assembly depends on these namespaces" check that several framework-specific
/// fitness tests run directly, outside the shared rule library.
/// </summary>
internal static class DependencyAssertions
{
    /// <summary>
    /// Runs the NetArchTest dependency rule: no type in <paramref name="assembly"/> may depend on any
    /// of <paramref name="forbiddenNamespaces"/>.
    /// </summary>
    public static NetArchTest.Rules.TestResult NoDependencyOnAny(Assembly assembly, params string[] forbiddenNamespaces) =>
        Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

    /// <summary>
    /// Fails, naming the violating types and <paramref name="reason"/>, when any type in
    /// <paramref name="assembly"/> depends on <paramref name="forbiddenNamespace"/>.
    /// </summary>
    public static void AssertNoDependency(Assembly assembly, string forbiddenNamespace, string reason) =>
        ArchitectureAssert.NoViolations(NoDependencyOnAny(assembly, forbiddenNamespace), reason);
}
