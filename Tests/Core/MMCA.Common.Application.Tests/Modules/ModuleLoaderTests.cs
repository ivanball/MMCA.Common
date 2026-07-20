using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using Moq;

namespace MMCA.Common.Application.Tests.Modules;

/// <summary>
/// Tests for <see cref="ModuleLoader"/> using fake modules declared in THIS assembly and the
/// <c>DiscoverAndRegister</c> overload that takes explicit assemblies to scan. The fakes use
/// static, benign-by-default configuration (see <see cref="FakeCycleModuleOne"/>) so that the
/// whole-assembly scan every test performs stays deterministic; tests within this class run
/// sequentially (xUnit default), so the static state is safe.
/// </summary>
public sealed class ModuleLoaderTests
{
    public ModuleLoaderTests()
    {
        FakeModuleTracker.Reset();
        FakeCycleModuleOne.ConfiguredDependencies = [];
        FakeCycleModuleTwo.ConfiguredDependencies = [];
    }

    private static ModulesSettings CreateModulesSettings(params (string Name, bool Enabled)[] modules)
    {
        var settings = new ModulesSettings();
        foreach (var (name, enabled) in modules)
        {
            settings[name] = new ModuleSettings { Enabled = enabled };
        }

        return settings;
    }

    private static ApplicationSettings CreateApplicationSettings() => new();

    private static void Discover(ModuleLoader loader, IServiceCollection services, ModulesSettings modulesSettings) =>
        loader.DiscoverAndRegister(
            services,
            new ConfigurationBuilder(),
            CreateApplicationSettings(),
            modulesSettings,
            environmentName: null,
            moduleAssemblies: [typeof(ModuleLoaderTests).Assembly]);

    // ── Kahn topological order: dependencies register before dependents ──
    [Fact]
    public void DiscoverAndRegister_RegistersDependenciesBeforeDependents()
    {
        var loader = new ModuleLoader();
        var services = new ServiceCollection();
        var modulesSettings = CreateModulesSettings(
            ("FakeCharlie", true),
            ("FakeBravo", true),
            ("FakeAlpha", true));

        Discover(loader, services, modulesSettings);

        // Charlie depends on Bravo which depends on Alpha; Charlie is declared FIRST in this
        // file, so only a real topological sort produces this order.
        FakeModuleTracker.RegistrationOrder.Should().Equal("FakeAlpha", "FakeBravo", "FakeCharlie");
        loader.EnabledModules.Select(m => m.Name).Should().Equal("FakeAlpha", "FakeBravo", "FakeCharlie");
    }

    // ── Circular dependency: throws naming the cycle ──
    [Fact]
    public void DiscoverAndRegister_CircularDependency_ThrowsNamingTheCycle()
    {
        FakeCycleModuleOne.ConfiguredDependencies = ["FakeCycleTwo"];
        FakeCycleModuleTwo.ConfiguredDependencies = ["FakeCycleOne"];
        var loader = new ModuleLoader();
        var modulesSettings = CreateModulesSettings(("FakeCycleOne", true), ("FakeCycleTwo", true));

        var act = () => Discover(loader, new ServiceCollection(), modulesSettings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*")
            .Which.Message.Should().ContainAll("FakeCycleOne", "FakeCycleTwo");
    }

    // ── RequiresDependencies: disabled, non-remote dependency throws ──
    [Fact]
    public void DiscoverAndRegister_RequiredDependencyDisabledAndNotRemote_Throws()
    {
        var loader = new ModuleLoader();

        // FakeStrict requires FakeAlpha, which is absent from settings (treated as disabled)
        // and not declared remote.
        var act = () => Discover(loader, new ServiceCollection(), CreateModulesSettings(("FakeStrict", true)));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll("FakeStrict", "FakeAlpha", "RemoteDependencies");
    }

    // ── Disabled module: stubs registered and name recorded ──
    [Fact]
    public void DiscoverAndRegister_DisabledModule_RegistersStubsAndRecordsName()
    {
        var loader = new ModuleLoader();
        var services = new ServiceCollection();
        var modulesSettings = CreateModulesSettings(("FakeStubbed", false));

        Discover(loader, services, modulesSettings);

        loader.DisabledModuleNames.Should().Contain("FakeStubbed");
        loader.EnabledModules.Should().NotContain(m => m.Name == "FakeStubbed");
        FakeModuleTracker.RegistrationOrder.Should().NotContain("FakeStubbed", "disabled modules must not Register()");
        services.Should().Contain(d =>
            d.ServiceType == typeof(IFakeRemoteContract) && d.ImplementationType == typeof(FakeRemoteContractStub));
    }

    // ── Remote dependency satisfies a strict module (no throw) ──
    [Fact]
    public void DiscoverAndRegister_RequiredDependencyDeclaredRemote_DoesNotThrow()
    {
        var loader = new ModuleLoader();
        var modulesSettings = CreateModulesSettings(("FakeConsumer", true), ("FakeStubbed", false));
        modulesSettings["FakeConsumer"].RemoteDependencies = ["FakeStubbed"];

        var act = () => Discover(loader, new ServiceCollection(), modulesSettings);

        act.Should().NotThrow("a disabled dependency declared in RemoteDependencies is satisfied externally");
        loader.EnabledModules.Should().Contain(m => m.Name == "FakeConsumer");
    }

    // ── ValidateRemoteDependencies: host wired the replacement → passes ──
    [Fact]
    public void ValidateRemoteDependencies_HostRewiredService_DoesNotThrow()
    {
        var (loader, services) = DiscoverConsumerWithRemoteStubbedDependency();
        services.Replace(ServiceDescriptor.Scoped<IFakeRemoteContract, FakeRemoteContractRealAdapter>());
        using var provider = services.BuildServiceProvider();

        var act = () => loader.ValidateRemoteDependencies(provider);

        act.Should().NotThrow();
    }

    // ── ValidateRemoteDependencies: service removed by the host → throws ──
    [Fact]
    public void ValidateRemoteDependencies_ServiceNoLongerResolvable_Throws()
    {
        var (loader, services) = DiscoverConsumerWithRemoteStubbedDependency();
        services.RemoveAll<IFakeRemoteContract>();
        using var provider = services.BuildServiceProvider();

        var act = () => loader.ValidateRemoteDependencies(provider);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll("FakeConsumer", "FakeStubbed", typeof(IFakeRemoteContract).FullName!);
    }

    // ── ValidateRemoteDependencies: still resolving to the stub → warns, no throw ──
    [Fact]
    public void ValidateRemoteDependencies_StillResolvesToStub_LogsWarningButDoesNotThrow()
    {
        var logger = new Mock<ILogger<ModuleLoader>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var (loader, services) = DiscoverConsumerWithRemoteStubbedDependency(logger.Object);
        using var provider = services.BuildServiceProvider();

        var act = () => loader.ValidateRemoteDependencies(provider);

        act.Should().NotThrow("an intentionally kept stub is a best-effort dependency, not a startup failure");
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>(e => e.Name == "LogRemoteDependencyStillStub"),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Runs a discover where FakeConsumer (enabled, strict) declares its disabled dependency
    /// FakeStubbed as remote, so FakeStubbed's stub registration is recorded for validation.
    /// </summary>
    private static (ModuleLoader Loader, ServiceCollection Services) DiscoverConsumerWithRemoteStubbedDependency(
        ILogger<ModuleLoader>? logger = null)
    {
        var loader = logger is null ? new ModuleLoader() : new ModuleLoader { Logger = logger };
        var services = new ServiceCollection();
        var modulesSettings = CreateModulesSettings(("FakeConsumer", true), ("FakeStubbed", false));
        modulesSettings["FakeConsumer"].RemoteDependencies = ["FakeStubbed"];

        Discover(loader, services, modulesSettings);

        return (loader, services);
    }

    // ── SeedAllAsync: runs discovered seeders of enabled modules ──
    [Fact]
    public async Task SeedAllAsync_EnabledModuleWithSeeder_RunsSeeder()
    {
        var loader = new ModuleLoader();
        var services = new ServiceCollection();
        Discover(loader, services, CreateModulesSettings(("FakeAlpha", true)));
        await using var provider = services.BuildServiceProvider();

        await loader.SeedAllAsync(provider, CancellationToken.None);

        FakeModuleTracker.RegistrationOrder.Should().Equal("FakeAlpha", "Seed:FakeAlpha");
    }

    [Fact]
    public async Task SeedAllAsync_ModuleDisabled_DoesNotRunItsSeeder()
    {
        var loader = new ModuleLoader();
        Discover(loader, new ServiceCollection(), CreateModulesSettings(("FakeAlpha", false)));

        await loader.SeedAllAsync(new Mock<IServiceProvider>().Object, CancellationToken.None);

        FakeModuleTracker.RegistrationOrder.Should().BeEmpty();
    }

    // ── ModulesSettings: IsModuleEnabled ──
    [Fact]
    public void IsModuleEnabled_WhenModuleExistsAndEnabled_ReturnsTrue()
    {
        var settings = CreateModulesSettings(("Catalog", true));

        settings.IsModuleEnabled("Catalog").Should().BeTrue();
    }

    [Fact]
    public void IsModuleEnabled_WhenModuleExistsAndDisabled_ReturnsFalse()
    {
        var settings = CreateModulesSettings(("Catalog", false));

        settings.IsModuleEnabled("Catalog").Should().BeFalse();
    }

    [Fact]
    public void IsModuleEnabled_WhenModuleDoesNotExist_ReturnsFalse()
    {
        var settings = new ModulesSettings();

        settings.IsModuleEnabled("NonExistent").Should().BeFalse();
    }

    // ── ModulesSettings: IsDependencyRemote ──
    [Fact]
    public void IsDependencyRemote_DeclaredRemote_IsCaseInsensitive()
    {
        var settings = CreateModulesSettings(("Sales", true));
        settings["Sales"].RemoteDependencies = ["Catalog"];

        settings.IsDependencyRemote("Sales", "catalog").Should().BeTrue();
        settings.IsDependencyRemote("Sales", "Identity").Should().BeFalse();
        settings.IsDependencyRemote("Unknown", "Catalog").Should().BeFalse();
    }
}

/// <summary>Records the order in which fake modules register (and seed), per test.</summary>
internal static class FakeModuleTracker
{
    public static List<string> RegistrationOrder { get; } = [];

    public static void Reset() => RegistrationOrder.Clear();
}

/// <summary>Cross-module contract stubbed by <see cref="FakeStubbedModule"/> when disabled.</summary>
public interface IFakeRemoteContract;

/// <summary>The disabled-module stub implementation.</summary>
public sealed class FakeRemoteContractStub : IFakeRemoteContract;

/// <summary>Stands in for the host-wired cross-process adapter (e.g. a typed gRPC client).</summary>
public sealed class FakeRemoteContractRealAdapter : IFakeRemoteContract;

// Deliberately declared before its dependencies so discovery order differs from dependency
// order and only a real topological sort passes the ordering test.
public sealed class FakeModuleCharlie : IModule
{
    public string Name => "FakeCharlie";

    public IReadOnlyList<string> Dependencies => ["FakeBravo"];

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}

public sealed class FakeModuleBravo : IModule
{
    public string Name => "FakeBravo";

    public IReadOnlyList<string> Dependencies => ["FakeAlpha"];

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}

public sealed class FakeModuleAlpha : IModule
{
    public string Name => "FakeAlpha";

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}

/// <summary>Seeder for <see cref="FakeModuleAlpha"/>; discovered alongside the modules.</summary>
public sealed class FakeModuleAlphaSeeder : IModuleSeeder
{
    public string ModuleName => "FakeAlpha";

    public Task SeedAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        FakeModuleTracker.RegistrationOrder.Add("Seed:FakeAlpha");
        return Task.CompletedTask;
    }
}

/// <summary>A module that refuses to start when a dependency is disabled and not remote.</summary>
public sealed class FakeStrictModule : IModule
{
    public string Name => "FakeStrict";

    public IReadOnlyList<string> Dependencies => ["FakeAlpha"];

    public bool RequiresDependencies => true;

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}

/// <summary>A module whose disabled stub registers <see cref="IFakeRemoteContract"/>.</summary>
public sealed class FakeStubbedModule : IModule
{
    public string Name => "FakeStubbed";

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);

    public void RegisterDisabledStubs(IServiceCollection services) =>
        services.AddScoped<IFakeRemoteContract, FakeRemoteContractStub>();
}

/// <summary>A strict consumer whose dependency is satisfied remotely in the validation tests.</summary>
public sealed class FakeConsumerModule : IModule
{
    public string Name => "FakeConsumer";

    public IReadOnlyList<string> Dependencies => ["FakeStubbed"];

    public bool RequiresDependencies => true;

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}

/// <summary>Cycle fakes: dependencies are static and benign-by-default (empty) so the
/// assembly-wide scan other tests perform never sees a cycle unless a test opts in.</summary>
public sealed class FakeCycleModuleOne : IModule
{
    internal static IReadOnlyList<string> ConfiguredDependencies { get; set; } = [];

    public string Name => "FakeCycleOne";

    public IReadOnlyList<string> Dependencies => ConfiguredDependencies;

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}

public sealed class FakeCycleModuleTwo : IModule
{
    internal static IReadOnlyList<string> ConfiguredDependencies { get; set; } = [];

    public string Name => "FakeCycleTwo";

    public IReadOnlyList<string> Dependencies => ConfiguredDependencies;

    public void Register(IServiceCollection services, IConfigurationBuilder configuration, ApplicationSettings applicationSettings) =>
        FakeModuleTracker.RegistrationOrder.Add(Name);
}
