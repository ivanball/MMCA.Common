using System.IdentityModel.Tokens.Jwt;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Lightweight client-side JWT inspection used by token storage to decide when an in-memory access token
/// must be re-acquired. No signature validation — the API validates every request.
/// </summary>
public static class JwtTokenInfo
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="token"/> is a readable JWT whose expiry is at
    /// least <paramref name="skew"/> in the future. A null, blank, unreadable, or (near-)expired token
    /// returns <see langword="false"/> so callers refresh proactively before the token actually expires.
    /// </summary>
    public static bool IsFresh(string? token, TimeSpan skew)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(token))
        {
            return false;
        }

        try
        {
            return handler.ReadJwtToken(token).ValidTo > DateTime.UtcNow + skew;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }
}
