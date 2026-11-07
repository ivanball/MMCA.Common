using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// Configuration for read-only view-as support access (ADR-137). Bound from the <c>ViewAs</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> While <see cref="Enabled"/> is false the grant and end endpoints answer
/// 404, the guard middleware (always in the pipeline) refuses any view-as token with 401, the grant
/// table is not in any model (so a host that never opts in sees no migration), and the sign-in
/// history shows no view-as rows.
/// </para>
/// <para>
/// A host that enables it must also register an <see cref="IViewAsSubjectResolver"/> (the account
/// facts a view-as token is minted from), grant <c>users:view-as</c> to its staff role, add the
/// <c>ViewAsGrants</c> migration on the Identity database, and disclose staff access in its privacy
/// notice.
/// </para>
/// <para>
/// <b>Multi-service hosts.</b> Only the Identity database holds the grant table, so only the Identity
/// service keeps <see cref="HostsGrantStore"/> (the default); every other service sets <c>Enabled</c>
/// true and <c>HostsGrantStore</c> false. Such a service maps no grant table and serves no grant or end
/// endpoint (404). Its guard keeps the read-only rule, refuses an expired or malformed token, and
/// refuses a token whose grant was ended, which it learns from a marker the Identity service's end
/// endpoint writes to the shared <c>ICacheService</c> (living for the token's remaining life plus the
/// JWT clock skew). Every service therefore needs the same distributed cache, and a service that cannot
/// read it refuses view-as requests (fail closed). A service with <c>Enabled</c> false keeps refusing
/// every view-as token.
/// </para>
/// </remarks>
public sealed class ViewAsSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ViewAs";

    /// <summary>Gets a value indicating whether view-as is available on this host. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether this host holds the grant store: maps the <c>ViewAsGrants</c>
    /// table, serves the grant and end endpoints, and checks each view-as request against the grant
    /// row. Defaults to <see langword="true"/>, which is right for a single host. In a multi-service
    /// host only the Identity service keeps it; every other service sets it to
    /// <see langword="false"/> and checks the shared cache's grant-ended marker instead.
    /// </summary>
    public bool HostsGrantStore { get; init; } = true;

    /// <summary>
    /// Gets how long a grant row is kept after it was issued, in days. The rows are the audit record
    /// of staff access; the default of 90 matches the field-level audit trail's retention.
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 90;

    /// <summary>
    /// Gets the logical data source that owns the grant table: the Identity database. Defaults to
    /// the engine's default source, which is right for a single-database host.
    /// </summary>
    [Required]
    public string DataSourceName { get; init; } = "Default";
}
