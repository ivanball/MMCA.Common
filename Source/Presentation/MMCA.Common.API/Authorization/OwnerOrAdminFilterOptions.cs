using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Host-configurable vocabulary for <see cref="OwnerOrAdminFilter"/> (ADR-033). The claim and route
/// parameter keep the framework's conventional names (<c>customer_id</c> and <c>id</c>); the bypass
/// role has no default, because the framework knows no role names. A host that applies the filter
/// configures its own through
/// <c>services.Configure&lt;OwnerOrAdminFilterOptions&gt;(o =&gt; o.BypassRole = MyRoleNames.Admin)</c>,
/// and one that never applies it configures nothing.
/// </summary>
public sealed class OwnerOrAdminFilterOptions
{
    /// <summary>The claim carrying the caller's owner identifier. Default: <c>customer_id</c>.</summary>
    public string OwnerClaimType { get; set; } = "customer_id";

    /// <summary>
    /// The role that bypasses the ownership check. Required: the options are validated against their
    /// data annotations on first resolve, so a host that applies the filter without naming a role
    /// fails loudly instead of silently bypassing for nobody.
    /// </summary>
    [Required]
    public string BypassRole { get; set; } = string.Empty;

    /// <summary>
    /// The parameter compared against the owner claim: a route value (<c>/customers/{id}</c>) or,
    /// when absent from the route, a model-bound query/body argument (<c>?userId=42</c>).
    /// Default: <c>id</c>.
    /// </summary>
    public string OwnerParameterName { get; set; } = "id";
}
