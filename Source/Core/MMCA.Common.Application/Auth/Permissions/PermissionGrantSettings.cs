using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Auth.Permissions;

/// <summary>
/// Configuration for the stored permission grants. Bound from the
/// <c>Authentication:PermissionGrants</c> configuration section.
/// </summary>
public sealed class PermissionGrantSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:PermissionGrants";

    /// <summary>
    /// How long a cached grant snapshot is served before it is reloaded, in seconds.
    /// </summary>
    /// <remarks>
    /// This is the bound on how stale another replica may be after an edit, since invalidation is
    /// per process. Five minutes is the default because a grant is an administrative change, not a
    /// per-request one; lowering it trades database reads for a shorter window.
    /// </remarks>
    [Range(5, 3600)]
    public int CacheSeconds { get; init; } = 300;

    /// <summary>
    /// The logical data source that owns the grant table. Defaults to the engine's default source,
    /// which is exactly right for a single-database host and is the name a modular host points at its
    /// Identity database.
    /// </summary>
    [Required]
    public string DataSourceName { get; init; } = "Default";

    /// <summary>
    /// The role names the administration surface lists, in addition to any role that already carries
    /// a stored grant.
    /// </summary>
    /// <remarks>
    /// It has to be configured because neither layer can enumerate roles on its own:
    /// <c>IPermissionRegistry</c> answers questions about a role but does not list them, and the grant
    /// table only knows the roles somebody has already granted something to. A host that leaves this
    /// empty still gets a working surface, it just lists nothing until the first grant exists.
    /// </remarks>
    public IReadOnlyList<string> KnownRoles { get; init; } = [];
}
