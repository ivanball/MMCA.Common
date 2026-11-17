using Microsoft.Extensions.Configuration;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Reads the "Modules" configuration section to decide whether a UI head registers a UI module, with
/// the rule the service hosts apply to their own modules (<c>ModulesSettings.IsModuleEnabled</c>,
/// ADR-059): a module is enabled only when <c>Modules:{name}</c> exists and its <c>Enabled</c> is not
/// <see langword="false"/> (<c>Enabled</c> defaults to <see langword="true"/> once the module's key
/// exists); a module missing from the section, or a missing section, is disabled.
/// </summary>
/// <remarks>
/// The WebAssembly client of a Blazor Web head gets the head's resolved flags through
/// <c>/client-config</c> (its <c>modules</c> section, loaded into configuration before the client
/// registers modules), so both halves of one head register the same modules.
/// </remarks>
public static class UIModuleConfiguration
{
    private const string ModulesSectionName = "Modules";

    /// <summary>
    /// Returns <see langword="true"/> when <c>Modules:{moduleName}</c> exists and its <c>Enabled</c>
    /// is not <see langword="false"/>. Key lookup is case-insensitive, as configuration keys are.
    /// </summary>
    /// <param name="configuration">The head's configuration.</param>
    /// <param name="moduleName">The module name, as its <c>Modules</c> entry spells it.</param>
    /// <returns><see langword="true"/> if the module is enabled.</returns>
    public static bool IsModuleEnabled(IConfiguration configuration, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        var section = configuration.GetSection(ModulesSectionName).GetSection(moduleName);
        return section.Exists() && section.GetValue("Enabled", true);
    }
}
