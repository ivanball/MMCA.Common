using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API;

/// <summary>
/// Removes controllers belonging to disabled modules from MVC's controller discovery.
/// </summary>
/// <remarks>
/// <para>
/// Matching strategy: a controller is filtered out when its assembly's simple name (or its
/// containing-type namespace) contains a token <c>.{ModuleName}.</c> for a module whose
/// <c>ModulesSettings:{ModuleName}:Enabled</c> is <see langword="false"/>. This handles both
/// the <c>MMCA.{Repo}.{Module}.API</c> convention (e.g. <c>MMCA.Store.Catalog.API</c> →
/// module <c>Catalog</c>) and the legacy <c>{Prefix}.Modules.{Module}.*</c> convention.
/// </para>
/// <para>
/// Required when the host project references a module's <c>API</c> assembly transitively
/// (so MVC's default convention discovers its controllers) but the operator has disabled
/// the module via <c>Modules:{Name}:Enabled=false</c>. Without filtering, MVC would map the
/// disabled module's controllers and any request to them would 500 because the module's
/// DI services were never registered.
/// </para>
/// </remarks>
/// <param name="modulesSettings">Configuration settings indicating which modules are enabled.</param>
public sealed class ModuleControllerFeatureProvider(
    ModulesSettings modulesSettings)
    : IApplicationFeatureProvider<ControllerFeature>
{
    /// <inheritdoc />
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        // Snapshot disabled module names once so we don't iterate the dictionary per-controller.
        var disabledModuleNames = modulesSettings
            .Where(kvp => !kvp.Value.Enabled)
            .Select(kvp => kvp.Key)
            .ToList();

        if (disabledModuleNames.Count == 0)
        {
            return;
        }

        var toRemove = feature.Controllers
            .Where(c => IsDisabledModuleController(c, disabledModuleNames))
            .ToList();

        foreach (var controller in toRemove)
        {
            feature.Controllers.Remove(controller);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the controller's assembly name or namespace
    /// contains a <c>.{ModuleName}.</c> segment for any disabled module.
    /// </summary>
    private static bool IsDisabledModuleController(
        TypeInfo controllerType,
        IReadOnlyList<string> disabledModuleNames)
    {
        var assemblyName = controllerType.Assembly.GetName().Name ?? string.Empty;
        var ns = controllerType.Namespace ?? string.Empty;

        foreach (var moduleName in disabledModuleNames)
        {
            // Match `.Catalog.` inside e.g. `MMCA.Store.Catalog.API` (assembly name) or
            // `MMCA.Store.Catalog.API.Controllers` (namespace). Wrap with dots so we don't
            // get false positives from substrings like "Catalogue".
            var token = $".{moduleName}.";

            if (assemblyName.Contains(token, StringComparison.OrdinalIgnoreCase)
                || ns.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
