using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Builds the claims-only token a browser receives when the host keeps every credential server-side
/// (<see cref="SessionCookieSettings.ClaimsOnlyBrowserTokens"/>). It is the access token's own payload
/// under an unsecured JWT header (<c>alg</c> <c>none</c>) with an empty signature: the client can still
/// read the user's claims and the expiry to render its authentication state, but the token authorizes
/// nothing, because every API validates signatures and rejects an unsigned token.
/// </summary>
public static class SessionClaimsToken
{
    // {"alg":"none","typ":"JWT"}, base64url without padding.
    private static readonly string UnsecuredHeader = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));

    /// <summary>
    /// Returns the claims-only form of <paramref name="accessToken"/>, or <see langword="null"/> when
    /// the value is not a readable compact JWT.
    /// </summary>
    /// <param name="accessToken">The signed access token.</param>
    /// <returns><c>header.payload.</c> with the unsecured header, or <see langword="null"/>.</returns>
    public static string? Create(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || !new JwtSecurityTokenHandler().CanReadToken(accessToken))
        {
            return null;
        }

        var parts = accessToken.Split('.');
        return parts.Length == 3 ? $"{UnsecuredHeader}.{parts[1]}." : null;
    }
}
