using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// Browser (Blazor Server + WebAssembly) token refresher. Calls the same-origin
/// <c>POST /auth/session/token</c> endpoint via JS <c>fetch</c> (<c>credentials:'same-origin'</c>) so the
/// browser sends its HttpOnly auth cookies; the UI host validates-or-refreshes server-side and returns
/// only the access token. The refresh token never reaches JS. Registered on the Web Server and WASM hosts.
/// </summary>
/// <remarks>
/// The script answers <see langword="null"/> only for the endpoint's 401 ("no session") and throws for
/// any other failure (a non-OK status such as 429 or 5xx, a network error), so
/// <see cref="TryAcquireAccessTokenAsync"/> can tell the two apart.
/// </remarks>
public sealed class SameOriginProxyTokenRefresher(IJSRuntime jsRuntime) : ISessionAwareTokenRefresher
{
    /// <inheritdoc />
    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) =>
        (await TryAcquireAccessTokenAsync(cancellationToken)).AccessToken;

    /// <inheritdoc />
    public async Task<TokenAcquisition> TryAcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await jsRuntime.InvokeAsync<string?>("mmcaAuthSession.getToken", cancellationToken);
            return string.IsNullOrWhiteSpace(token) ? TokenAcquisition.NoSession : TokenAcquisition.Acquired(token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSDisconnectedException or JSException or OperationCanceledException)
        {
            // JS interop unavailable (SSR prerender / disconnected circuit), a cancelled call, or the
            // token endpoint failing transiently: none of these says anything about the session. The
            // server-side cookie path handles the interop-less phases.
            return TokenAcquisition.Unavailable;
        }
    }
}
