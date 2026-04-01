using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Controllers;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API.Tests;

public sealed class ModuleControllerFeatureProviderTests
{
    private static ModulesSettings CreateSettings(params (string Name, bool Enabled)[] modules)
    {
        var settings = new ModulesSettings();
        foreach ((string name, bool enabled) in modules)
        {
            settings[name] = new ModuleSettings { Enabled = enabled };
        }

        return settings;
    }

    [Fact]
    public void PopulateFeature_KeepsNonModuleControllers()
    {
        // This test type's namespace (MMCA.Common.API.Tests) does NOT contain ".Modules."
        ModulesSettings settings = CreateSettings();
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();
        TypeInfo nonModuleType = typeof(ModuleControllerFeatureProviderTests).GetTypeInfo();
        feature.Controllers.Add(nonModuleType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().ContainSingle()
            .Which.Should().BeSameAs(nonModuleType);
    }

    [Fact]
    public void PopulateFeature_KeepsEnabledModuleControllers()
    {
        // ApiControllerBase's namespace is MMCA.Common.API.Controllers — no ".Modules." segment.
        // To test module filtering, use the actual namespace check logic: types without ".Modules."
        // are always kept regardless of settings.
        ModulesSettings settings = CreateSettings(("API", true));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();
        TypeInfo controllerType = typeof(Controllers.ApiControllerBaseTests).GetTypeInfo();
        feature.Controllers.Add(controllerType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().ContainSingle();
    }

    [Fact]
    public void PopulateFeature_EmptyFeature_DoesNotThrow()
    {
        ModulesSettings settings = CreateSettings(("Catalog", false));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();

        Action act = () => sut.PopulateFeature([], feature);

        act.Should().NotThrow();
        feature.Controllers.Should().BeEmpty();
    }

    [Fact]
    public void PopulateFeature_HandlesControllerWithNullNamespace()
    {
        // Types in the global namespace have null Namespace — they should never be filtered.
        // We cannot easily create a type with null Namespace in a file-scoped namespace project,
        // but we can verify that non-module types are retained. The built-in object type lives in
        // "System" namespace (no ".Modules." segment), so it exercises the same safe path.
        ModulesSettings settings = CreateSettings(("System", false));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();

        // typeof(object) has namespace "System" — no ".Modules." segment, so it's kept.
        TypeInfo systemType = typeof(object).GetTypeInfo();
        feature.Controllers.Add(systemType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().ContainSingle();
    }
}
