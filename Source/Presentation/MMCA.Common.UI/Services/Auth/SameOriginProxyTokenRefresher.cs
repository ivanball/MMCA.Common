using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Browser (Blazor Server + WebAssembly) token refresher. Calls the same-origin
/// <c>POST /auth/session/token</c> endpoint via JS <c>fetch</c> (<c>credentials:'same-origin'</c>) so the
/// browser sends its HttpOnly auth cookies; the UI host validates-or-refreshes server-side and returns
/// only the access token. The refresh token never reaches JS. Registered on the Web Server and WASM hosts.
/// </summary>
public sealed class SameOriginProxyTokenRefresher(IJSRuntime jsRuntime) : ITokenRefresher
{
    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await jsRuntime.InvokeAsync<string?>("mmcaAuthSession.getToken", cancellationToken);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSDisconnectedException or JSException or OperationCanceledException)
        {
            // JS interop unavailable (SSR prerender / disconnected circuit) — the server-side cookie path
            // handles those phases; here we simply report "no token acquired".
            return null;
        }
    }
}
