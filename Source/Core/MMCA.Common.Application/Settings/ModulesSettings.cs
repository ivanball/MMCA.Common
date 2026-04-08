namespace MMCA.Common.Application.Settings;

/// <summary>
/// Dictionary of module names to their settings, bound from the "Modules" configuration section.
/// Controls which modules are enabled at startup.
/// </summary>
public sealed class ModulesSettings : Dictionary<string, ModuleSettings>
{
    /// <summary>The configuration section name this class binds to.</summary>
    public static readonly string SectionName = "Modules";

    /// <summary>
    /// Returns <see langword="true"/> if the named module exists in configuration and is enabled.
    /// Modules not present in configuration are treated as disabled.
    /// </summary>
    /// <param name="moduleName">The module name to check.</param>
    /// <returns><see langword="true"/> if the module is enabled.</returns>
    public bool IsModuleEnabled(string moduleName)
        => TryGetValue(moduleName, out var settings) && settings.Enabled;

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="consumerModule"/> declares
    /// <paramref name="dependencyModule"/> as a remote dependency (satisfied by an extracted
    /// service rather than an in-process module). Used by the module loader to bypass the
    /// strict <c>RequiresDependencies</c> check for cross-process satisfaction.
    /// </summary>
    /// <param name="consumerModule">The module that depends on <paramref name="dependencyModule"/>.</param>
    /// <param name="dependencyModule">The dependency name to check.</param>
    /// <returns><see langword="true"/> if the dependency is declared as remote.</returns>
    public bool IsDependencyRemote(string consumerModule, string dependencyModule) =>
        TryGetValue(consumerModule, out var settings)
        && settings.RemoteDependencies.Contains(dependencyModule, StringComparer.OrdinalIgnoreCase);
}
