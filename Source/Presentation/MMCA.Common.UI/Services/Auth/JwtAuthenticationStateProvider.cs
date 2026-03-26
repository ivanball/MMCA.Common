using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Custom <see cref="AuthenticationStateProvider"/> that derives auth state from a JWT stored via
/// <see cref="ITokenStorageService"/>. Claims are extracted client-side without server validation,
/// keeping the UI responsive; the WebAPI performs full token validation on every request.
/// </summary>
public sealed class JwtAuthenticationStateProvider(ITokenStorageService tokenStorageService) : AuthenticationStateProvider
{
    private static readonly AuthenticationState AnonymousState =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <summary>
    /// Reads the stored JWT, validates it is parseable and not expired, and returns an
    /// <see cref="AuthenticationState"/> built from the token's claims. Falls back to anonymous
    /// on any failure (missing token, corrupt data, expired token, JS interop unavailable).
    /// </summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await tokenStorageService.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                return AnonymousState;
            }

            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
            {
                return AnonymousState;
            }

            var jwtToken = handler.ReadJwtToken(token);
            if (jwtToken.ValidTo < DateTime.UtcNow)
            {
                return AnonymousState;
            }

            // "jwt" authentication type makes the identity IsAuthenticated == true
            var identity = new ClaimsIdentity(jwtToken.Claims, "jwt");
            var principal = new ClaimsPrincipal(identity);
            return new AuthenticationState(principal);
        }
        catch
        {
            return AnonymousState;
        }
    }

    /// <summary>
    /// Pushes a new authenticated state to all <c>CascadingAuthenticationState</c> consumers
    /// immediately after login or token refresh, avoiding a full page reload.
    /// </summary>
    public void NotifyUserAuthentication(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(token);
        var identity = new ClaimsIdentity(jwtToken.Claims, "jwt");
        var principal = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));
    }

    /// <summary>
    /// Resets auth state to anonymous after logout, triggering UI updates across all components.
    /// </summary>
    public void NotifyUserLogout() =>
        NotifyAuthenticationStateChanged(Task.FromResult(AnonymousState));
}
