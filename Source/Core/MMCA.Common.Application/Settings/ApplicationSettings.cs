using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Settings;

/// <summary>
/// Global application settings bound from the "ApplicationSettings" configuration section.
/// </summary>
public sealed class ApplicationSettings
{
    /// <summary>The configuration section name this class binds to.</summary>
    public static readonly string SectionName = "ApplicationSettings";

    /// <summary>Whether MiniProfiler performance tracing is enabled.</summary>
    public bool UseMiniProfiler { get; init; }

    /// <summary>Maximum allowed page size for paginated queries.</summary>
    public int MaxPageSize { get; init; } = 500;

    /// <summary>
    /// Maximum number of rows a single CSV export may stream before it stops and marks itself
    /// truncated. The export endpoint page-loops the query service at <see cref="MaxPageSize"/> per
    /// page, so this is the ceiling on the whole file, not on one page.
    /// </summary>
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

    /// <summary>
    /// Controls the database initialization strategy on startup.
    /// <list type="bullet">
    ///   <item><c>"Migrate"</c> applies pending EF Core migrations (development/testing).</item>
    ///   <item><c>"None"</c> skips initialization and throws if pending migrations exist (production).</item>
    /// </list>
    /// Any other value fails startup.
    /// </summary>
    public string DatabaseInitStrategy { get; init; } = "Migrate";
}
