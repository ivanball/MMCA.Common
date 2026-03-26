using Microsoft.Extensions.Configuration;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Reads the "Modules" configuration section to determine whether a UI module should be registered.
/// Defaults to enabled when the section or module entry is absent, so existing deployments
/// without a Modules section continue to work.
/// </summary>
public static class UIModuleConfiguration
{
    private const string ModulesSectionName = "Modules";

    /// <summary>
    /// Returns <see langword="true"/> if the named module is enabled (or if no configuration exists for it).
    /// Checks <c>Modules:{moduleName}:Enabled</c> in the configuration hierarchy.
    /// </summary>
    public static bool IsModuleEnabled(IConfiguration configuration, string moduleName)
    {
        var section = configuration.GetSection(ModulesSectionName).GetSection(moduleName);
        return !section.Exists() || section.GetValue("Enabled", true);
    }
}
