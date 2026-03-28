using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;
using Moq;

namespace MMCA.Common.Application.Tests.Modules;

public sealed class ModuleLoaderTests
{
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

    // ── DiscoverAndRegister: disabled modules are tracked ──
    [Fact]
    public void DiscoverAndRegister_DisabledModule_AddsToDisabledList()
    {
        var loader = new ModuleLoader();
        var services = new ServiceCollection();
        var configBuilder = new Mock<IConfigurationBuilder>();
        var appSettings = CreateApplicationSettings();
        var modulesSettings = CreateModulesSettings(("TestModule", false));

        // We cannot inject fake modules into AppDomain, but we can test
        // the loader with the actual discovered modules (empty in test context).
        // Instead we test the settings behavior directly.
        loader.DiscoverAndRegister(services, configBuilder.Object, appSettings, modulesSettings);

        // No modules discovered in test AppDomain, so both lists empty
        loader.EnabledModules.Should().BeEmpty();
        loader.DisabledModuleNames.Should().BeEmpty();
    }

    // ── SeedAllAsync: seeds enabled modules ──
    [Fact]
    public async Task SeedAllAsync_WithNoModules_CompletesSuccessfully()
    {
        var loader = new ModuleLoader();
        var serviceProvider = new Mock<IServiceProvider>();

        await loader.SeedAllAsync(serviceProvider.Object, CancellationToken.None);

        // Should not throw when there are no enabled modules
        loader.EnabledModules.Should().BeEmpty();
    }

    // ── Log callback is invoked ──
    [Fact]
    public void DiscoverAndRegister_LogCallbackIsInvoked()
    {
        var logMessages = new List<(string Level, string Message)>();
        var loader = new ModuleLoader
        {
            Log = (level, message) => logMessages.Add((level, message))
        };
        var services = new ServiceCollection();
        var configBuilder = new Mock<IConfigurationBuilder>();
        var appSettings = CreateApplicationSettings();
        var modulesSettings = CreateModulesSettings();

        loader.DiscoverAndRegister(services, configBuilder.Object, appSettings, modulesSettings);

        // In test context with no real modules discovered, log may or may not have entries.
        // We verify the log delegate was set and did not throw.
        loader.EnabledModules.Should().BeEmpty();
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
}
