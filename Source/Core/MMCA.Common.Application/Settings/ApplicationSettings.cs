using System.ComponentModel.DataAnnotations;

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
    /// <remarks>
    /// 100,000 rows is roughly a 10-25 MB file for a typical grid DTO: large enough that no real
    /// operational export hits the cap, small enough that one caller cannot pin a request thread to a
    /// full-table scan. This is the only property on this class carrying a validation attribute today;
    /// it is honored by hosts that opt into <c>ValidateDataAnnotations</c> on the options binding, and
    /// the export endpoint independently falls back to this default when a host configures a
    /// non-positive value.
    /// </remarks>
    [Range(1, 10_000_000)]
    public int MaxExportRows { get; init; } = 100_000;

    /// <inheritdoc />
    public string DatabaseInitStrategy { get; init; } = "Migrate";
}
