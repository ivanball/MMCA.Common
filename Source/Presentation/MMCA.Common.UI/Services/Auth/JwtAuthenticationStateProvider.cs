using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Caching;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Custom <see cref="AuthenticationStateProvider"/> that derives auth state from a JWT stored via
/// <see cref="ITokenStorageService"/>. Claims are extracted client-side without server validation,
/// keeping the UI responsive; the WebAPI performs full token validation on every request.
/// <para>
/// When the storage reports that a held session ended on the server
/// (<see cref="ISessionAwareTokenStorage.SessionEnded"/>: revoked from another device, or the account
/// deleted), the provider empties the read cache and signs the shell out (A-34), the same local
/// sign-out <see cref="AuthUIService"/> performs for a session it can no longer refresh.
/// </para>
/// </summary>
public sealed class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState AnonymousState =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly ITokenStorageService _tokenStorageService;
    private readonly IUiReadCache? _readCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="JwtAuthenticationStateProvider"/> class without a
    /// read cache. Kept so code compiled against the original one-argument constructor keeps binding.
    /// </summary>
    /// <param name="tokenStorageService">The store the access token is read from.</param>
    public JwtAuthenticationStateProvider(ITokenStorageService tokenStorageService)
        : this(tokenStorageService, readCache: null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JwtAuthenticationStateProvider"/> class.</summary>
    /// <param name="tokenStorageService">The store the access token is read from.</param>
    /// <param name="readCache">
    /// Optional client-side read cache, emptied when the storage reports a session ended on the
    /// server: its entries were read under that session.
    /// </param>
    public JwtAuthenticationStateProvider(ITokenStorageService tokenStorageService, IUiReadCache? readCache)
    {
        ArgumentNullException.ThrowIfNull(tokenStorageService);
        _tokenStorageService = tokenStorageService;
        _readCache = readCache;

        // Same DI scope and lifetime as the storage, so the subscription never outlives either.
        if (tokenStorageService is ISessionAwareTokenStorage sessionAware)
        {
            sessionAware.SessionEnded += OnSessionEnded;
        }
    }

    /// <summary>
    /// Reads the stored JWT, validates it is parseable and not expired, and returns an
    /// <see cref="AuthenticationState"/> built from the token's claims. Falls back to anonymous
    /// on any failure (missing token, corrupt data, expired token, JS interop unavailable).
    /// </summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await _tokenStorageService.GetAccessTokenAsync();
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

    private void OnSessionEnded(object? sender, EventArgs e)
    {
        _readCache?.Clear();
        NotifyUserLogout();
    }
}
