using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// A leaf module fixture: it overrides neither <c>Dependencies</c> nor <c>RequiresDependencies</c>, so the
/// conformance base can only read them by dispatching to <c>IModule</c>'s DEFAULT implementations. That is
/// the behaviour the hand-written consumer tests needed an explicit <c>(IModule)</c> cast for, and the one
/// thing that would break silently across the package boundary.
/// </summary>
public sealed class FakeLeafModule : IModule
{
    public string Name => "FakeLeaf";

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings)
    {
    }
}

/// <summary>Cross-module export contract used to prove the disabled-stub hook reaches the real container.</summary>
public interface IFakeExportService;

/// <summary>The stub a disabled <see cref="FakeDependentModule"/> leaves behind.</summary>
public sealed class DisabledFakeExportService : IFakeExportService;

/// <summary>A dependent module fixture: declares dependencies, requires them, and registers a disabled stub.</summary>
public sealed class FakeDependentModule : IModule
{
    public string Name => "FakeDependent";

    public IReadOnlyList<string> Dependencies => ["FakeLeaf", "FakeOther"];

    public bool RequiresDependencies => true;

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings)
    {
    }

    public void RegisterDisabledStubs(IServiceCollection services) =>
        services.AddSingleton<IFakeExportService, DisabledFakeExportService>();
}

/// <summary>
/// Runs the shared <see cref="ModuleConformanceTestsBase{TModule}"/> against a leaf module, i.e. exactly the
/// subclass shape the three byte-identical consumer <c>{X}ModuleTests</c> files collapse into.
/// </summary>
public sealed class FakeLeafModuleConformanceTests : ModuleConformanceTestsBase<FakeLeafModule>
{
    protected override string ExpectedName => "FakeLeaf";
}

/// <summary>
/// Runs the shared base against a module that declares dependencies, requires them, and exports a stub,
/// i.e. the two consumer subclasses (Store Sales, ADC Notification) that are not leaves.
/// </summary>
public sealed class FakeDependentModuleConformanceTests : ModuleConformanceTestsBase<FakeDependentModule>
{
    protected override string ExpectedName => "FakeDependent";

    protected override IReadOnlyCollection<string> ExpectedDependencies => ["FakeOther", "FakeLeaf"];

    protected override bool ExpectedRequiresDependencies => true;

    protected override void AssertDisabledStubs(FakeDependentModule module)
    {
        var services = new ServiceCollection();

        module.RegisterDisabledStubs(services);

        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IFakeExportService));
        descriptor.Should().NotBeNull();
        descriptor.ImplementationType.Should().Be<DisabledFakeExportService>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}

/// <summary>
/// Adversarial coverage for the shared base itself: each assertion must actually FAIL on the drift it
/// claims to catch. The drifted subclasses are private so xUnit does not collect their inherited facts as
/// (deliberately failing) tests of their own.
/// </summary>
public class ModuleConformanceTestsBaseTests
{
    [Fact]
    public void Base_Fails_WhenTheNameDrifts()
    {
        var assert = new DriftedTests().Module_ShouldDeclare_ExpectedName;

        assert.Should().Throw<Exception>();
    }

    [Fact]
    public void Base_Fails_WhenADependencyIsMissing()
    {
        var assert = new DriftedTests().Module_ShouldDeclare_ExpectedDependencies;

        assert.Should().Throw<Exception>();
    }

    [Fact]
    public void Base_Fails_WhenRequiresDependenciesDrifts()
    {
        var assert = new DriftedTests().Module_ShouldDeclare_ExpectedRequiresDependencies;

        assert.Should().Throw<Exception>();
    }

    [Fact]
    public void Base_ReadsDefaultInterfaceImplementations_ForALeafModule()
    {
        // The leaf fixture declares neither member, so passing here proves the base reached IModule's
        // defaults rather than the (absent) members on the concrete type.
        var conformance = new FakeLeafModuleConformanceTests();

        conformance.Module_ShouldDeclare_ExpectedDependencies();
        conformance.Module_ShouldDeclare_ExpectedRequiresDependencies();
    }

    [Fact]
    public void Base_DisabledStubHook_IsVacuous_ByDefault()
    {
        var assert = new FakeLeafModuleConformanceTests().Module_ShouldRegister_ExpectedDisabledStubs;

        assert.Should().NotThrow();
    }

    private sealed class DriftedTests : ModuleConformanceTestsBase<FakeDependentModule>
    {
        protected override string ExpectedName => "NotTheDeclaredName";

        protected override IReadOnlyCollection<string> ExpectedDependencies => ["FakeLeaf"];

        protected override bool ExpectedRequiresDependencies => false;
    }
}
