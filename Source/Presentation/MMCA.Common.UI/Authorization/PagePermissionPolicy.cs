namespace MMCA.Common.UI.Authorization;

/// <summary>
/// Naming for the page permission policies <see cref="HasPagePermissionAttribute"/> refers to. The
/// prefix differs from the API's <c>perm:</c> on purpose: the API's policy provider, which a web host
/// may install by replacement, intercepts every <c>perm:</c> name, while a <c>page-perm:</c> name
/// falls through to the default provider and so to the policy <c>AddUIShared</c> registered.
/// </summary>
public static class PagePermissionPolicy
{
    /// <summary>The policy-name prefix that marks a page permission policy.</summary>
    public const string Prefix = "page-perm:";

    /// <summary>Builds the policy name for a permission.</summary>
    /// <param name="permission">The permission name (for example <c>conference:sessions:manage</c>).</param>
    /// <returns>The corresponding policy name.</returns>
    public static string NameFor(string permission) => Prefix + permission;
}
