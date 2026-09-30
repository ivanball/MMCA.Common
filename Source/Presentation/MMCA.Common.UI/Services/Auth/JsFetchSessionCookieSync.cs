using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// <see cref="ISessionCookieSync"/> implementation that fires a browser fetch via JS interop.
/// The fetch is issued from the browser (not the server), so the resulting <c>Set-Cookie</c>
/// lands in the user's cookie jar in both Blazor Server interactive mode and WebAssembly.
/// Reports the outcome: the script returns whether the endpoint answered 2xx (the clear is tried
/// twice), and an unavailable JS interop (SSR prerender, render-mode transition, disconnected
/// circuit) reports <see langword="false"/>.
/// </summary>
public sealed class JsFetchSessionCookieSync(IJSRuntime jsRuntime) : ISessionCookieSync
{
    private static bool IsInteropUnavailable(Exception ex) =>
        ex is InvalidOperationException or JSDisconnectedException or JSException or OperationCanceledException;

    /// <inheritdoc />
    public async Task<bool> SyncAsync(string accessToken, string refreshToken)
    {
        try
        {
            return await jsRuntime.InvokeAsync<bool>("mmcaAuthCookie.set", accessToken, refreshToken);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // JS interop unavailable (SSR prerender, disconnected circuit): nothing was written.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync()
    {
        try
        {
            return await jsRuntime.InvokeAsync<bool>("mmcaAuthCookie.clear");
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // JS interop unavailable: the cookie was not cleared by this call.
            return false;
        }
    }
}
