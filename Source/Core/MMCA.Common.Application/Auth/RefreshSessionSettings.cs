using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Configuration for multi-device refresh sessions. Bound from the <c>RefreshSessions</c>
/// configuration section; a host that omits the section gets the defaults.
/// </summary>
public sealed class RefreshSessionSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RefreshSessions";

    /// <summary>
    /// Maximum live sessions one user may hold at once. Signing in on device number
    /// <c>MaxActiveSessionsPerUser + 1</c> revokes that user's oldest live session rather than
    /// refusing the login, so the table cannot grow without bound while a legitimate sign-in never
    /// fails. Ten covers a realistic device set (phone, tablet, two laptops, a few browsers) with room
    /// to spare.
    /// </summary>
    [Range(1, 1000)]
    public int MaxActiveSessionsPerUser { get; init; } = 10;

    /// <summary>
    /// Logical data source whose database holds the <c>RefreshSessions</c> table, for a host that
    /// splits its modules across databases and keeps Identity on a named source. The default is the
    /// engine's <c>Default</c> source, which is what a single-database host uses. Ignored when the
    /// consumer ships an entity configuration for the session entity, since the data-source registry
    /// then routes it like any other entity.
    /// </summary>
    [MinLength(1)]
    public string DataSourceName { get; init; } = "Default";
}
