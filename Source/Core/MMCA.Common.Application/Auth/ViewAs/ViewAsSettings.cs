using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// Configuration for read-only view-as support access (ADR-137). Bound from the <c>ViewAs</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> While <see cref="Enabled"/> is false the grant and end endpoints answer
/// 404, the guard middleware is not added, the grant table is not in any model (so a host that never
/// opts in sees no migration), and the sign-in history shows no view-as rows.
/// </para>
/// <para>
/// A host that enables it must also register an <see cref="IViewAsSubjectResolver"/> (the account
/// facts a view-as token is minted from), grant <c>users:view-as</c> to its staff role, add the
/// <c>ViewAsGrants</c> migration on the Identity database, and disclose staff access in its privacy
/// notice.
/// </para>
/// </remarks>
public sealed class ViewAsSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ViewAs";

    /// <summary>Gets a value indicating whether view-as is available on this host. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; init; }

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
