using AwesomeAssertions;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.Application.Tests.Settings;

public sealed class ModulesSettingsTests
{
    // ── IsModuleEnabled ──
    [Fact]
    public void IsModuleEnabled_WhenModuleExistsAndEnabled_ReturnsTrue()
    {
        var settings = new ModulesSettings
        {
            ["Catalog"] = new ModuleSettings { Enabled = true }
        };

        settings.IsModuleEnabled("Catalog").Should().BeTrue();
    }

    [Fact]
    public void IsModuleEnabled_WhenModuleExistsButDisabled_ReturnsFalse()
    {
        var settings = new ModulesSettings
        {
            ["Catalog"] = new ModuleSettings { Enabled = false }
        };

        settings.IsModuleEnabled("Catalog").Should().BeFalse();
    }

    [Fact]
    public void IsModuleEnabled_WhenModuleNotPresent_ReturnsFalse()
    {
        var settings = new ModulesSettings();

        settings.IsModuleEnabled("NonExistent").Should().BeFalse();
    }

    [Fact]
    public void IsModuleEnabled_WithDefaultModuleSettings_ReturnsTrue()
    {
        var settings = new ModulesSettings
        {
            ["Sales"] = new ModuleSettings()
        };

        settings.IsModuleEnabled("Sales").Should().BeTrue();
    }

    [Fact]
    public void IsModuleEnabled_IsCaseSensitive()
    {
        var settings = new ModulesSettings
        {
            ["Catalog"] = new ModuleSettings { Enabled = true }
        };

        settings.IsModuleEnabled("catalog").Should().BeFalse();
    }

    // ── SectionName ──
    [Fact]
    public void SectionName_IsModules() =>
        ModulesSettings.SectionName.Should().Be("Modules");

    // ── Dictionary behavior ──
    [Fact]
    public void ModulesSettings_InheritsDictionaryBehavior()
    {
        var settings = new ModulesSettings
        {
            ["A"] = new ModuleSettings { Enabled = true },
            ["B"] = new ModuleSettings { Enabled = false }
        };

        settings.Should().HaveCount(2);
        settings.ContainsKey("A").Should().BeTrue();
    }

    // ── ModuleSettings defaults ──
    [Fact]
    public void ModuleSettings_EnabledDefaultsToTrue() =>
        new ModuleSettings().Enabled.Should().BeTrue();
}
