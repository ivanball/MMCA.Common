using MMCA.Common.Shared.Auth.Permissions;

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
    /// principal's roles via <see cref="IPermissionRegistry"/>.
    /// <para>
    /// The framework's token service emits them: every access token it mints carries one claim of
    /// this type per permission the registry grants the token's role, sorted ordinally so the same
    /// role always produces the same token shape. That is what lets a client decide what to show
    /// (a navigation entry gating on <c>NavItem.RequiredPermission</c>) without knowing any role
    /// name. Server-side checks still go through the registry, so a host that mints tokens some
    /// other way loses nothing.
    /// </para>
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

    /// <summary>
    /// The claim asserting that the access token was minted after a second authentication factor was
    /// presented. It is an <c>amr</c>-style claim (RFC 8176, "authentication methods references"):
    /// the VALUE names the method that satisfied the challenge, one of
    /// <see cref="MultiFactorMethodTotp"/>, <see cref="MultiFactorMethodRecoveryCode"/> or
    /// <see cref="MultiFactorMethodPasskey"/>.
    /// <para>
    /// <b>Presence is the assertion, not the value.</b> Readers ask whether the claim is there
    /// (<see cref="ClaimsPrincipalExtensions.HasMultiFactor"/>); the value exists for audit and for a
    /// UI that wants to say "you signed in with a recovery code". A token issued for an account that
    /// has no second factor carries no <c>mfa</c> claim at all, which is why a request marked
    /// <c>IRequiresMfa</c> denies such a caller rather than degrading to a role check.
    /// </para>
    /// </summary>
    public const string MultiFactor = "mfa";

    /// <summary>Value of <see cref="MultiFactor"/> when a time-based one-time code satisfied the challenge.</summary>
    public const string MultiFactorMethodTotp = "otp";

    /// <summary>Value of <see cref="MultiFactor"/> when a single-use recovery code satisfied the challenge.</summary>
    public const string MultiFactorMethodRecoveryCode = "recovery";

    /// <summary>
    /// Value of <see cref="MultiFactor"/> when a passkey (a WebAuthn assertion, ADR-139) signed the user
    /// in. A passkey is possession of the device plus the authenticator's own user verification, so the
    /// sign-in is multi-factor by itself, and a refresh carries the value over like the other two.
    /// </summary>
    public const string MultiFactorMethodPasskey = "passkey";

    /// <summary>
    /// The claim carrying the instant of the interactive sign-in a token descends from, in seconds since
    /// the Unix epoch (the OpenID Connect <c>auth_time</c> claim). Stamped when a sign-in opens a session
    /// (password, sign-in link, external login, passkey, registration) and carried UNCHANGED across every
    /// refresh, so unlike <c>iat</c> it does not move when a refresh mints a new access token. A view-as
    /// token carries none.
    /// <para>
    /// It is what "recently signed in" is judged by: registering a passkey requires an
    /// <c>auth_time</c> within the last ten minutes (ADR-139), so a stolen refresh token cannot keep a
    /// session fresh enough to plant one. A token minted before the claim shipped has none and reads as
    /// not recent.
    /// </para>
    /// </summary>
    public const string AuthTime = "auth_time";

    /// <summary>
    /// The claim naming the staff user behind a view-as token (ADR-137): the token's <c>sub</c> is the
    /// user being viewed, and this claim's value is the id of the staff user who is viewing.
    /// <para>
    /// <b>Presence makes the request read-only.</b> Every command is refused and every non-safe HTTP
    /// method is rejected while it is present, so nothing can be written in the viewed user's name.
    /// Readers ask whether it is there (<see cref="ClaimsPrincipalExtensions.IsViewAs"/>).
    /// </para>
    /// </summary>
    public const string ViewAsActor = "act_sub";

    /// <summary>
    /// The id of the view-as grant a view-as token was minted for (ADR-137). Checked on every
    /// view-as request, so ending the grant ends the token before it expires.
    /// </summary>
    public const string ViewAsGrant = "vag";
}
