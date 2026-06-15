using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Always-anonymous <see cref="AuthenticationStateProvider"/>. The gallery never logs in; the auth
/// pages and shared layout render in their signed-out state (Login/Register links shown, no user
/// menu) so axe scans the unauthenticated markup.
/// </summary>
internal sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(Anonymous);
}
