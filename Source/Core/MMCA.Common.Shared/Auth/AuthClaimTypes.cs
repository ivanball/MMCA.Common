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

    /// <summary>
    /// The JWT <c>sid</c> claim (RFC 7519 / OpenID Connect "session id"): the identifier of the
    /// refresh session the access token was minted for. It names the <b>device</b> behind the token,
    /// which is what lets a "your devices" list mark the row the caller is looking at as the current
    /// one, and what lets a per-device sign-out know which session it is signing out of.
    /// <para>
    /// <b>Additive, never required.</b> Rotation mints a new session and therefore a new <c>sid</c>,
    /// and a token issued before this claim shipped simply carries none. Nothing validates it: a
    /// missing or unparsable value degrades to "no current session known", never to a rejected token.
    /// </para>
    /// </summary>
    public const string SessionId = "sid";
}
