using Microsoft.AspNetCore.Authorization;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Requires the authenticated principal to hold a specific permission to access the decorated
/// controller or action. The permission is resolved against the registered
/// <see cref="MMCA.Common.Shared.Auth.IPermissionRegistry"/> (role-derived) or an explicit
/// permission claim. Prefer this over role-based <c>[Authorize(Policy = ...)]</c> checks so
/// endpoints depend on capabilities, not role names.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    /// <summary>Initializes the attribute requiring the specified permission.</summary>
    /// <param name="permission">The permission name (e.g. <c>"sessions:manage"</c>).</param>
    public HasPermissionAttribute(string permission)
        : base(PermissionPolicy.NameFor(permission)) => Permission = permission;

    /// <summary>Gets the required permission name.</summary>
    public string Permission { get; }
}
