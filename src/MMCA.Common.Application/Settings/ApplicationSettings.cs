namespace MMCA.Common.Application.Settings;

/// <summary>
/// Global application settings bound from the "ApplicationSettings" configuration section.
/// </summary>
public sealed class ApplicationSettings : IApplicationSettings
{
    /// <summary>The configuration section name this class binds to.</summary>
    public static readonly string SectionName = "ApplicationSettings";

    /// <inheritdoc />
    public bool UseMiniProfiler { get; init; }

    /// <inheritdoc />
    public int MaxPageSize { get; init; } = 500;

    /// <inheritdoc />
    public string DatabaseInitStrategy { get; init; } = "Migrate";
}
