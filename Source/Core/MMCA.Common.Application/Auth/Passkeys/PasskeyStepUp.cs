using System.Globalization;
using System.Security.Claims;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The recent-sign-in rule for registering a passkey (ADR-139): a new way into the account may be
/// added only by a caller whose access token is under <see cref="MaxTokenAge"/> old, or was minted
/// after a second factor (it carries the <c>mfa</c> claim). A stolen, long-lived session cannot quietly
/// plant its own passkey.
/// </summary>
public static class PasskeyStepUp
{
    /// <summary>The oldest an access token may be (by its <c>iat</c> claim) to register without <c>mfa</c>.</summary>
    public static readonly TimeSpan MaxTokenAge = TimeSpan.FromMinutes(10);

    /// <summary>The JWT issued-at claim (RFC 7519), seconds since the Unix epoch.</summary>
    private const string IssuedAtClaim = "iat";

    /// <summary>Whether <paramref name="principal"/> may register a passkey at <paramref name="now"/>.</summary>
    /// <param name="principal">The caller.</param>
    /// <param name="now">The current instant.</param>
    /// <returns><see langword="true"/> when the token is fresh or carries <c>mfa</c>.</returns>
    public static bool IsSatisfiedBy(ClaimsPrincipal? principal, DateTimeOffset now)
    {
        if (principal.HasMultiFactor())
        {
            return true;
        }

        var issuedAt = principal?.FindFirst(IssuedAtClaim)?.Value;
        if (!long.TryParse(issuedAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        // A token stamped slightly in the future (clock skew between hosts) counts as fresh.
        return now - DateTimeOffset.FromUnixTimeSeconds(seconds) <= MaxTokenAge;
    }
}
