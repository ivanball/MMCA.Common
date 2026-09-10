using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// One stored "this role grants this permission" row: the data half of the authorization model, laid
/// over the compiled <c>IPermissionRegistry</c> so an operator can widen a role without a deploy.
/// <para>
/// <b>Grants only, never denials.</b> A row adds a capability; there is no revoke row. Removing a
/// stored capability is deleting its row, and a permission the code compiled in cannot be taken away
/// by data at all. That asymmetry is deliberate: a deny row would make the effective permission set
/// depend on evaluation order, and would let a data edit silently disable an endpoint the code
/// guarantees.
/// </para>
/// <para>
/// <b>Framework bookkeeping, not an aggregate.</b> Like <see cref="RefreshSession"/>, this is a flat
/// record with no audit stamps, no soft-delete flag and no concurrency token: rows are inserted and
/// deleted, never edited, and no global query filter should hide one from an authorization decision.
/// It is mapped only where a consumer opts in
/// (<c>ApplyPermissionGrantConfiguration</c>), because grants belong to the Identity module's
/// database rather than to every data source.
/// </para>
/// </summary>
public sealed class PermissionGrant
{
    /// <summary>Column width for <see cref="Role"/>, matching the role names a token can carry.</summary>
    public const int RoleMaxLength = 64;

    /// <summary>Column width for <see cref="Permission"/>.</summary>
    public const int PermissionMaxLength = 128;

    /// <summary>Gets the unique identifier for this grant row.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets the role the permission is granted to. Compared case-insensitively everywhere it is read,
    /// matching <c>PermissionRegistry</c> and <c>RoleValue</c>.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Gets the granted permission, in the <c>area:capability</c> shape the registry uses. Compared
    /// ordinally, again matching the compiled registry.
    /// </summary>
    public required string Permission { get; init; }

    /// <summary>Gets the UTC instant the grant was created, kept for the audit trail.</summary>
    public required DateTime GrantedAt { get; init; }

    /// <summary>
    /// Gets who created the grant, when the caller supplied it. Informational: it is never part of an
    /// authorization decision, so a row written by a migration with no principal is still honored.
    /// </summary>
    public string? GrantedBy { get; init; }

    /// <summary>
    /// Creates a grant row, trimming and validating the two values that make up its identity.
    /// </summary>
    /// <param name="role">The role receiving the permission.</param>
    /// <param name="permission">The permission being granted.</param>
    /// <param name="grantedAt">The UTC instant the grant was created.</param>
    /// <param name="grantedBy">Optional principal name recorded for audit.</param>
    /// <returns>The grant, or a validation failure.</returns>
    public static Result<PermissionGrant> Create(
        string role,
        string permission,
        DateTime grantedAt,
        string? grantedBy = null)
    {
        if (string.IsNullOrWhiteSpace(role) || role.Length > RoleMaxLength)
        {
            return Result.Failure<PermissionGrant>(Error.Validation(
                "PermissionGrant.RoleInvalid",
                "A permission grant requires a role name of at most 64 characters.",
                nameof(Create)));
        }

        if (string.IsNullOrWhiteSpace(permission) || permission.Length > PermissionMaxLength)
        {
            return Result.Failure<PermissionGrant>(Error.Validation(
                "PermissionGrant.PermissionInvalid",
                "A permission grant requires a permission name of at most 128 characters.",
                nameof(Create)));
        }

        return Result.Success(new PermissionGrant
        {
            Role = role.Trim(),
            Permission = permission.Trim(),
            GrantedAt = grantedAt,
            GrantedBy = grantedBy,
        });
    }
}
