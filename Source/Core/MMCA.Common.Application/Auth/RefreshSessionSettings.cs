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
    /// Gets a value indicating whether the <c>RefreshSessions</c> table belongs in the model.
    /// Defaults to <see langword="false"/>, so a host that has not opted in keeps the model it had
    /// before sessions shipped and its migrations never see the table (the <c>Scheduler:Enabled</c>
    /// precedent). The Identity service sets this to <see langword="true"/>; every other service
    /// leaves it alone, which is what keeps the table in exactly one database.
    /// </summary>
    public bool Enabled { get; init; }

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
    /// engine's <c>Default</c> source, which is what a single-database host uses.
    /// <para>
    /// It answers two questions at once, and they must agree: which context maps the table (only the
    /// context instance whose physical source carries this name does, so the other databases in a
    /// multi-source host keep an unchanged model), and which context the shipped
    /// <c>IRefreshSessionStore</c> reads and writes through. Setting it to a source that does not
    /// exist fails loudly on the first session query rather than reading the wrong database. It is
    /// ignored for routing when the consumer ships its own entity configuration for the session
    /// entity, since the data-source registry then places it like any other entity.
    /// </para>
    /// </summary>
    [MinLength(1)]
    public string DataSourceName { get; init; } = "Default";
}
