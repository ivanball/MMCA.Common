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

    /// <summary>
    /// The JWT <c>sub</c> claim: the single authoritative carrier of the user identifier in every
    /// token the framework mints. Readers must not assume the raw name survives the pipeline: the
    /// JWT bearer handler maps <c>sub</c> onto <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
    /// by default, while a handler reading a token's claims directly (the session-cookie handler)
    /// leaves it unmapped. <see cref="ClaimsPrincipalExtensions.FindUserIdValue"/> reads both forms,
    /// and is what every framework reader uses.
    /// </summary>
    public const string Subject = "sub";
}
