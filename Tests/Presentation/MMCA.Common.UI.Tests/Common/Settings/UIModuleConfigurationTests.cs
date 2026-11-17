using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Tests.Common.Settings;

/// <summary>
/// A10: <see cref="UIModuleConfiguration.IsModuleEnabled"/> applies the service hosts' rule
/// (<c>ModulesSettings.IsModuleEnabled</c>, ADR-059): a module is on only when its <c>Modules</c>
/// entry exists and does not set <c>Enabled</c> to false, so a module missing from configuration is
/// off on the UI heads exactly as it is on the service hosts.
/// </summary>
public sealed class UIModuleConfigurationTests
{
    [Fact]
    public void AModuleMissingFromTheSection_IsDisabled() =>
        UIModuleConfiguration.IsModuleEnabled(Configuration(("Modules:Identity:Enabled", "true")), "Catalog")
            .Should().BeFalse("a module the configuration does not name is off, as on the service hosts");

    [Fact]
    public void AMissingModulesSection_DisablesEveryModule() =>
        UIModuleConfiguration.IsModuleEnabled(Configuration(), "Identity")
            .Should().BeFalse("no Modules section names no module");

    [Fact]
    public void AModuleWithEnabledTrue_IsEnabled() =>
        UIModuleConfiguration.IsModuleEnabled(Configuration(("Modules:Identity:Enabled", "true")), "Identity")
            .Should().BeTrue();

    [Fact]
    public void AModuleThatExistsWithoutAnEnabledKey_IsEnabled() =>
        UIModuleConfiguration.IsModuleEnabled(Configuration(("Modules:Sales:RemoteDependencies:0", "Identity")), "Sales")
            .Should().BeTrue("Enabled defaults to true once the module's key exists");

    [Fact]
    public void AModuleWithEnabledFalse_IsDisabled() =>
        UIModuleConfiguration.IsModuleEnabled(Configuration(("Modules:Catalog:Enabled", "false")), "Catalog")
            .Should().BeFalse();

    [Theory]
    [InlineData("identity")]
    [InlineData("IDENTITY")]
    public void TheModuleName_MatchesCaseInsensitively_AsConfigurationKeysDo(string moduleName) =>
        UIModuleConfiguration.IsModuleEnabled(Configuration(("Modules:Identity:Enabled", "true")), moduleName)
            .Should().BeTrue("configuration binding treats keys case-insensitively");

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();
}
