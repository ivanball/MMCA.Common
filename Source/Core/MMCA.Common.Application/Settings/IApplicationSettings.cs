namespace MMCA.Common.Application.Settings;

/// <summary>
/// Abstraction for global application settings, injected into services that need
/// to read configuration without depending on the concrete <see cref="ApplicationSettings"/> class.
/// </summary>
public interface IApplicationSettings
{
    /// <summary>Whether MiniProfiler performance tracing is enabled.</summary>
    bool UseMiniProfiler { get; init; }

    /// <summary>Maximum allowed page size for paginated queries.</summary>
    int MaxPageSize { get; init; }

    /// <summary>
    /// Maximum number of rows a single CSV export may stream before it stops and marks itself
    /// truncated. The export endpoint page-loops the query service at <see cref="MaxPageSize"/> per
    /// page, so this is the ceiling on the whole file, not on one page.
    /// </summary>
    int MaxExportRows { get; init; }

    /// <summary>
    /// Controls database initialization strategy on startup.
    /// <list type="bullet">
    ///   <item><c>"Migrate"</c> — applies pending EF Core migrations (development/testing).</item>
    ///   <item><c>"EnsureCreated"</c> — legacy <c>EnsureCreated</c> behavior.</item>
    ///   <item><c>"None"</c> — skips initialization; throws if pending migrations exist (production).</item>
    /// </list>
    /// </summary>
    string DatabaseInitStrategy { get; init; }
}
