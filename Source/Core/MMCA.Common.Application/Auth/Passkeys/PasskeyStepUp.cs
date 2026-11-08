using System.Security.Claims;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The recent-sign-in rule for registering a passkey (ADR-139): a new way into the account may be added
/// only by a caller whose session signed in interactively within the last <see cref="MaxSignInAge"/>, by
/// its <c>auth_time</c> claim.
/// <para>
/// <b>Not the token's age, and not <c>mfa</c>.</b> Both ride every refresh (a refresh resets <c>iat</c>
/// and carries <c>mfa</c> over), so either would let a stolen refresh token keep a session "fresh" enough
/// to plant its own passkey forever. <c>auth_time</c> is carried over UNCHANGED, so only a new sign-in
/// renews it. A token with no <c>auth_time</c> (minted before the claim shipped) never qualifies.
/// </para>
/// </summary>
public static class PasskeyStepUp
{
    /// <summary>The oldest a sign-in (its <c>auth_time</c>) may be for its session to register a passkey.</summary>
    public static readonly TimeSpan MaxSignInAge = TimeSpan.FromMinutes(10);

    /// <summary>Whether <paramref name="principal"/> may register a passkey at <paramref name="now"/>.</summary>
    /// <param name="principal">The caller.</param>
    /// <param name="now">The current instant.</param>
    /// <returns><see langword="true"/> when the caller signed in within <see cref="MaxSignInAge"/>.</returns>
    public static bool IsSatisfiedBy(ClaimsPrincipal? principal, DateTimeOffset now) =>
        principal.FindAuthTime() is { } signedInAt

        // A sign-in stamped slightly in the future (clock skew between hosts) counts as recent.
        && now - signedInAt <= MaxSignInAge;
}
