using MMCA.Common.Architecture.Tests.Helpers;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Enforces Clean Architecture layer dependency rules for MMCA.Common packages.
/// Allowed dependency flow: Shared -> Domain -> Application -> Infrastructure -> API/UI.
/// Each layer may only reference layers below it in this stack.
/// </summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void Shared_ShouldNotDependOn_Domain() =>
        AssertNoDependency(PackageAssemblies.Shared, "MMCA.Common.Domain",
            "Shared is the foundation layer — it must not depend on Domain");

    [Fact]
    public void Shared_ShouldNotDependOn_Application() =>
        AssertNoDependency(PackageAssemblies.Shared, "MMCA.Common.Application",
            "Shared must not depend on Application");

    [Fact]
    public void Shared_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(PackageAssemblies.Shared, "MMCA.Common.Infrastructure",
            "Shared must not depend on Infrastructure");

    [Fact]
    public void Shared_ShouldNotDependOn_Api() =>
        AssertNoDependency(PackageAssemblies.Shared, "MMCA.Common.API",
            "Shared must not depend on API");

    [Fact]
    public void Domain_ShouldNotDependOn_Application() =>
        AssertNoDependency(PackageAssemblies.Domain, "MMCA.Common.Application",
            "Domain must not depend on Application — Domain is the innermost layer");

    [Fact]
    public void Domain_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(PackageAssemblies.Domain, "MMCA.Common.Infrastructure",
            "Domain must not depend on Infrastructure");

    [Fact]
    public void Domain_ShouldNotDependOn_Api() =>
        AssertNoDependency(PackageAssemblies.Domain, "MMCA.Common.API",
            "Domain must not depend on API/Presentation");

    [Fact]
    public void Application_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(PackageAssemblies.Application, "MMCA.Common.Infrastructure",
            "Application must not depend on Infrastructure — use repository/UoW abstractions");

    [Fact]
    public void Application_ShouldNotDependOn_Api() =>
        AssertNoDependency(PackageAssemblies.Application, "MMCA.Common.API",
            "Application must not depend on API/Presentation");

    [Fact]
    public void Infrastructure_ShouldNotDependOn_Api() =>
        AssertNoDependency(PackageAssemblies.Infrastructure, "MMCA.Common.API",
            "Infrastructure must not depend on API — Infrastructure implements Application interfaces, not Presentation");

    [Fact]
    public void UI_ShouldNotDependOn_Application() =>
        AssertNoDependency(PackageAssemblies.UI, "MMCA.Common.Application",
            "UI must not depend on Application — UI depends only on Shared for WASM compatibility");

    [Fact]
    public void UI_ShouldNotDependOn_Infrastructure() =>
        AssertNoDependency(PackageAssemblies.UI, "MMCA.Common.Infrastructure",
            "UI must not depend on Infrastructure");

    [Fact]
    public void UI_ShouldNotDependOn_Domain() =>
        AssertNoDependency(PackageAssemblies.UI, "MMCA.Common.Domain",
            "UI must not depend on Domain — UI depends only on Shared for WASM compatibility");

    private static void AssertNoDependency(Assembly assembly, string forbiddenNamespace, string reason)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn(forbiddenNamespace)
            .GetResult();

        ArchitectureTestHelper.AssertNoViolations(result, reason);
    }
}
