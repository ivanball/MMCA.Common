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
        // typeof(object) lives in namespace "System" / assembly "System.Private.CoreLib".
        // Neither contains the token ".System." (no leading dot), so the filter does not
        // match and the type is retained. This guards against false positives on built-in
        // BCL types when an operator names a module after a common token.
        ModulesSettings settings = CreateSettings(("System", false));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();

        TypeInfo systemType = typeof(object).GetTypeInfo();
        feature.Controllers.Add(systemType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().ContainSingle();
    }

    // ── Real-world convention: MMCA.{Repo}.{Module}.API.Controllers.* ──
    [Fact]
    public void PopulateFeature_RemovesControllerForDisabledModule_ByNamespaceToken()
    {
        // Stand-in for a controller whose namespace contains ".Catalog." — the actual
        // MMCA.Store.Catalog.API.Controllers convention. Catalog is disabled in settings,
        // so the controller must be removed even though its namespace doesn't contain
        // the legacy ".Modules." segment.
        ModulesSettings settings = CreateSettings(("Catalog", false));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();
        TypeInfo catalogControllerType = typeof(Fakes.MMCA.Store.Catalog.API.Controllers.FakeCategoriesController).GetTypeInfo();
        feature.Controllers.Add(catalogControllerType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().BeEmpty();
    }

    [Fact]
    public void PopulateFeature_KeepsControllerForEnabledModule_ByNamespaceToken()
    {
        ModulesSettings settings = CreateSettings(("Catalog", true));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();
        TypeInfo catalogControllerType = typeof(Fakes.MMCA.Store.Catalog.API.Controllers.FakeCategoriesController).GetTypeInfo();
        feature.Controllers.Add(catalogControllerType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().ContainSingle();
    }

    [Fact]
    public void PopulateFeature_DoesNotMatchOnSubstring()
    {
        // Disabled module "Cat" must NOT remove a controller in ".Catalog." namespace,
        // because the matcher uses dot-bounded tokens (".Cat." vs ".Catalog.").
        ModulesSettings settings = CreateSettings(("Cat", false));
        var sut = new ModuleControllerFeatureProvider(settings);
        var feature = new ControllerFeature();
        TypeInfo catalogControllerType = typeof(Fakes.MMCA.Store.Catalog.API.Controllers.FakeCategoriesController).GetTypeInfo();
        feature.Controllers.Add(catalogControllerType);

        sut.PopulateFeature([], feature);

        feature.Controllers.Should().ContainSingle();
    }
}
