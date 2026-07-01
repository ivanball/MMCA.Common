namespace MMCA.Common.API.Authorization;

/// <summary>
/// Host-configurable vocabulary for <see cref="OwnerOrAdminFilter"/> (ADR-033). The defaults match the
/// original hard-coded behavior (MMCA.Store's <c>customer_id</c> claim, <c>Admin</c> bypass role and
/// <c>id</c> route parameter), so existing hosts need no configuration; a host with a different
/// ownership vocabulary (e.g. a <c>UserId</c> claim with an <c>Organizer</c> bypass role keyed by a
/// <c>userId</c> route value) configures this via
/// <c>services.Configure&lt;OwnerOrAdminFilterOptions&gt;(...)</c>.
/// </summary>
public sealed class OwnerOrAdminFilterOptions
{
    /// <summary>The claim carrying the caller's owner identifier. Default: <c>customer_id</c>.</summary>
    public string OwnerClaimType { get; set; } = "customer_id";

    /// <summary>The role that bypasses the ownership check. Default: <c>Admin</c>.</summary>
    public string BypassRole { get; set; } = "Admin";

    /// <summary>
    /// The parameter compared against the owner claim: a route value (<c>/customers/{id}</c>) or,
    /// when absent from the route, a model-bound query/body argument (<c>?userId=42</c>).
    /// Default: <c>id</c>.
    /// </summary>
    public string OwnerParameterName { get; set; } = "id";
}
