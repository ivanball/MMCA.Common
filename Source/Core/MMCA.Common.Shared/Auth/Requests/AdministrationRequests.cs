namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for replacing the set of roles an account holds.
/// </summary>
/// <remarks>
/// A replace rather than an add/remove pair: the administration UI edits a list, and a replace makes
/// the request idempotent (re-submitting the same list is a no-op) where an add/remove pair would
/// need the caller to know what the account already had.
/// </remarks>
/// <param name="Roles">The complete set of roles the account should hold afterwards.</param>
public sealed record SetUserRolesRequest(IReadOnlyList<string> Roles);

/// <summary>
/// Request payload for replacing the stored permission grants of one role.
/// </summary>
/// <remarks>
/// These are the STORED grants only. Permissions compiled into the host's
/// <c>IPermissionRegistry</c> are not editable and are not returned here, so an operator can never
/// remove a capability the code depends on by editing data.
/// </remarks>
/// <param name="Permissions">The complete set of stored permissions the role should grant afterwards.</param>
public sealed record SetRolePermissionsRequest(IReadOnlyList<string> Permissions);
