namespace MMCA.Common.Application.Settings;

/// <summary>
/// Per-module configuration settings.
/// </summary>
public sealed class ModuleSettings
{
    /// <summary>Whether this module should be registered and activated at startup. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; init; } = true;
}
