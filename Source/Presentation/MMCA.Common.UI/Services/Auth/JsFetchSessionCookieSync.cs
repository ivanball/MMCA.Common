using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// <see cref="ISessionCookieSync"/> implementation that fires a browser fetch via JS interop.
/// The fetch is issued from the browser (not the server), so the resulting <c>Set-Cookie</c>
/// lands in the user's cookie jar in both Blazor Server interactive mode and WebAssembly.
/// Falls silent when JS interop is unavailable (SSR prerender, render-mode transition).
/// </summary>
public sealed class JsFetchSessionCookieSync(IJSRuntime jsRuntime) : ISessionCookieSync
{
    private static bool IsInteropUnavailable(Exception ex) =>
        ex is InvalidOperationException or JSDisconnectedException or JSException or OperationCanceledException;

    public async Task SyncAsync(string accessToken, string refreshToken)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("mmcaAuthCookie.set", accessToken, refreshToken);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // JS interop unavailable (SSR prerender, disconnected circuit) — cookie will be synced on next write.
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("mmcaAuthCookie.clear");
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // JS interop unavailable — cookie will still be cleared when the user next logs in or the token expires.
        }
    }
}
