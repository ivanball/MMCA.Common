namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Custom claim types used by the framework's authorization model, alongside the standard
/// <see cref="System.Security.Claims.ClaimTypes"/> values.
/// </summary>
public static class AuthClaimTypes
{
    /// <summary>
    /// Claim type carrying a single granted permission. A principal may carry zero or more
    /// permission claims; they are honored <b>in addition to</b> permissions derived from the
    /// principal's roles via <see cref="IPermissionRegistry"/>. Baking permission claims into the
    /// token is optional — role-derived permissions work without them.
    /// </summary>
    public const string Permission = "permission";
}
