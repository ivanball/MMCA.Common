using Microsoft.AspNetCore.Authorization;

namespace MMCA.Common.UI.Authorization;

/// <summary>
/// Requires a signed-in caller whose token carries the given <c>permission</c> claim to open the
/// decorated Blazor page, in every UI head (web, WebAssembly and MAUI). It is the page-side twin of
/// <see cref="Common.NavItem.RequiredPermission"/>: both check the claim with the same
/// <c>HasPermissionClaim</c>, so a menu entry gated on a permission and the page it opens cannot
/// disagree.
/// <para>
/// No registration is needed: <c>AddUIShared</c> registers one policy per permission declared on a
/// component in <c>MMCA.Common.UI</c> or in any registered UI module's assembly. The check reads the
/// claim only (a UI head has no permission registry), so it suits a host whose token issuer stamps
/// permission claims; a role-only token never passes.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class HasPagePermissionAttribute : AuthorizeAttribute
{
    /// <summary>Initializes the attribute requiring the specified permission.</summary>
    /// <param name="permission">The permission name (for example <c>conference:sessions:manage</c>).</param>
    public HasPagePermissionAttribute(string permission)
        : base(PagePermissionPolicy.NameFor(permission)) => Permission = permission;

    /// <summary>Gets the required permission name.</summary>
    public string Permission { get; }
}
