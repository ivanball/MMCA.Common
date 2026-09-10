using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.Permissions;

/// <summary>
/// Reads and writes the stored <see cref="PermissionGrant"/> rows that layer over the host's compiled
/// permission registry.
/// </summary>
/// <remarks>
/// Reads are whole-set on purpose (<see cref="GetAllAsync"/>): the authorization decision itself is
/// synchronous, so the effective grants are cached in memory and refreshed as one snapshot rather
/// than queried per check. A per-role query would put a database round trip on the hot path of every
/// permission-gated request.
/// </remarks>
public interface IPermissionGrantStore
{
    /// <summary>
    /// Reads every stored grant. Used to build the in-memory snapshot the authorization path reads.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every grant row.</returns>
    Task<IReadOnlyList<PermissionGrant>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the permissions stored for one role, for the administration surface that edits them.
    /// </summary>
    /// <param name="role">The role name, matched case-insensitively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The permissions granted to that role by stored rows.</returns>
    Task<IReadOnlyList<string>> GetPermissionsAsync(string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants a permission to a role. Idempotent: granting a permission the role already has stored
    /// succeeds and writes nothing, because an administration UI clicked twice is not an error.
    /// </summary>
    /// <param name="role">The role receiving the permission.</param>
    /// <param name="permission">The permission to grant.</param>
    /// <param name="grantedBy">Optional principal name recorded for audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the entity's validation failure.</returns>
    Task<Result> GrantAsync(
        string role,
        string permission,
        string? grantedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stored grant. Idempotent: removing one that is not there succeeds.
    /// </summary>
    /// <remarks>
    /// This removes a STORED grant only. A permission the host compiled into its registry keeps being
    /// granted, and no row can take it away; that is what keeps a data edit from disabling an endpoint
    /// the code guarantees.
    /// </remarks>
    /// <param name="role">The role losing the permission.</param>
    /// <param name="permission">The permission to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result.</returns>
    Task<Result> RevokeAsync(string role, string permission, CancellationToken cancellationToken = default);
}
