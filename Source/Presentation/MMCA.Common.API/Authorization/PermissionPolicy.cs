namespace MMCA.Common.API.Authorization;

/// <summary>
/// Naming conventions for permission-based authorization policies. A permission such as
/// <c>"sessions:manage"</c> is exposed to the ASP.NET Core authorization system as the policy
/// name <c>"perm:sessions:manage"</c>; <see cref="PermissionPolicyProvider"/> materializes such
/// policies on demand.
/// </summary>
public static class PermissionPolicy
{
    /// <summary>The policy-name prefix that marks a permission policy.</summary>
    public const string Prefix = "perm:";

    /// <summary>Builds the policy name for a permission.</summary>
    /// <param name="permission">The permission name (e.g. <c>"sessions:manage"</c>).</param>
    /// <returns>The corresponding policy name (e.g. <c>"perm:sessions:manage"</c>).</returns>
    public static string NameFor(string permission) => Prefix + permission;
}
