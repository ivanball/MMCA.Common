using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Gallery <see cref="AuthenticationStateProvider"/> that mirrors the request's authentication in
/// both render phases (replacing the former always-anonymous stub, which could not represent the
/// <c>gallery_auth</c>-cookie signed-in state the guarded notification pages now need):
/// during static SSR it reads the <see cref="HttpContext"/> user (authenticated by
/// <see cref="GalleryFakeAuthenticationHandler"/> when the cookie is present), and for interactive
/// server circuits the framework hands the handshake user in via
/// <see cref="IHostEnvironmentAuthenticationStateProvider"/>. Without the cookie both phases yield
/// anonymous, preserving the deliberate signed-out chrome of the login/register/components scans.
/// </summary>
internal sealed class GalleryAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
    : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private Task<AuthenticationState>? _hostState;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_hostState is not null)
        {
            return _hostState;
        }

        var user = httpContextAccessor.HttpContext?.User;
        return user?.Identity?.IsAuthenticated == true
            ? Task.FromResult(new AuthenticationState(user))
            : Task.FromResult(Anonymous);
    }

    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask) =>
        _hostState = authenticationStateTask;
}
