using Microsoft.AspNetCore.Authorization;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Authorization requirement satisfied when the principal holds <see cref="Permission"/> —
/// either as an explicit permission claim or via one of its roles (see
/// <see cref="PermissionAuthorizationHandler"/>).
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>Initializes the requirement for the specified permission.</summary>
    /// <param name="permission">The permission the principal must hold.</param>
    public PermissionRequirement(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    /// <summary>Gets the required permission.</summary>
    public string Permission { get; }
}
