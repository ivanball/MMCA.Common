using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API;

/// <summary>
/// Removes controllers belonging to disabled modules from MVC's controller discovery.
/// Derives the module name from the controller's namespace using the convention:
/// <c>{Prefix}.Modules.{ModuleName}.*</c> (e.g., <c>ADC.Modules.Catalog.API.Controllers</c>
/// maps to module name "Catalog").
/// </summary>
/// <remarks>
/// Controllers outside the <c>*.Modules.*</c> namespace (e.g., common or host controllers)
/// are never filtered. This runs once at startup during application part discovery.
/// The <paramref name="moduleNamespaceSegment"/> defaults to <c>".Modules."</c> and matches
/// any namespace containing that segment, making it application-prefix-agnostic.
/// </remarks>
/// <param name="modulesSettings">Configuration settings indicating which modules are enabled.</param>
/// <param name="moduleNamespaceSegment">
/// The namespace segment that identifies module controllers.
/// Defaults to <c>".Modules."</c> which matches any <c>{Prefix}.Modules.{ModuleName}.*</c> convention.
/// </param>
public sealed class ModuleControllerFeatureProvider(
    ModulesSettings modulesSettings,
    string moduleNamespaceSegment = ".Modules.")
    : IApplicationFeatureProvider<ControllerFeature>
{
    /// <inheritdoc />
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        var toRemove = feature.Controllers
            .Where(IsDisabledModuleController)
            .ToList();

        foreach (var controller in toRemove)
        {
            feature.Controllers.Remove(controller);
        }
    }

    /// <summary>
    /// Checks whether a controller belongs to a disabled module by extracting the module name
    /// from the first segment after the modules namespace marker.
    /// </summary>
    /// <param name="controllerType">The controller type info to evaluate.</param>
    /// <returns><see langword="true"/> if the controller belongs to a disabled module.</returns>
    private bool IsDisabledModuleController(TypeInfo controllerType)
    {
        var ns = controllerType.Namespace ?? string.Empty;

        var segmentIndex = ns.IndexOf(moduleNamespaceSegment, StringComparison.OrdinalIgnoreCase);
        if (segmentIndex < 0)
            return false;

        var remaining = ns[(segmentIndex + moduleNamespaceSegment.Length)..];
        var dotIndex = remaining.IndexOf('.', StringComparison.Ordinal);
        var moduleName = dotIndex >= 0 ? remaining[..dotIndex] : remaining;

        return !modulesSettings.IsModuleEnabled(moduleName);
    }
}
