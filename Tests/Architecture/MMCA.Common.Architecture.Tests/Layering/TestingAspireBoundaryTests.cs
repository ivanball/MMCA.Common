using MMCA.Common.Testing.Architecture;
using MMCA.Common.Testing.Aspire.Fixtures;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// Runtime half of the <c>MMCA.Common.Testing.Aspire</c> layer boundary. The compile-time half is
/// <c>EnforceTestingAspireLayerBoundary</c> in
/// <c>Source/Build/MMCA.Common.LayerEnforcement.targets</c>, which rejects a forbidden
/// <c>ProjectReference</c>; this half asserts the same rule against the compiled assembly, so a type
/// that arrives through a transitive path rather than a declared reference is caught too.
/// <para>
/// The rule matters because the package is AppHost-tier test infrastructure that consumers take
/// alongside their smoke tier. Letting it reach the Domain, Infrastructure or API layers directly
/// would make the fastest-growing package in a consumer's test graph a backdoor around every layer
/// rule the framework enforces on production code. It reaches Application and API only through
/// <c>MMCA.Common.Testing</c>, which is a deliberate, single, reviewable edge.
/// </para>
/// </summary>
public sealed class TestingAspireBoundaryTests
{
    private static Assembly TestingAspire => typeof(AppHostFixtureBase).Assembly;

    [Fact]
    public void TestingAspire_ShouldNotDependOn_Domain() =>
        AssertNoDependency(
            "MMCA.Common.Domain",
            "AppHost test infrastructure must not couple to the Domain layer");

    [Fact]
    public void TestingAspire_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(
            "MMCA.Common.Infrastructure",
            "AppHost test infrastructure must not couple to the Infrastructure layer");

    [Fact]
    public void TestingAspire_ShouldNotDependOn_Api() =>
        AssertNoDependency(
            "MMCA.Common.API",
            "AppHost test infrastructure reaches API only transitively through MMCA.Common.Testing");

    [Fact]
    public void TestingAspire_ShouldNotDependOn_Ui() =>
        AssertNoDependency(
            "MMCA.Common.UI",
            "AppHost test infrastructure drives services over HTTP, so it has no business seeing the UI layer");

    [Fact]
    public void TestingAspire_ShouldNotDependOn_ServiceDefaults() =>
        AssertNoDependency(
            "MMCA.Common.Aspire.",
            "the probe paths are mirrored constants precisely so this package never pulls the service-defaults graph");

    private static void AssertNoDependency(string forbiddenNamespace, string reason)
    {
        var result = Types.InAssembly(TestingAspire)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespace)
            .GetResult();

        ArchitectureAssert.NoViolations(result, reason);
    }
}
