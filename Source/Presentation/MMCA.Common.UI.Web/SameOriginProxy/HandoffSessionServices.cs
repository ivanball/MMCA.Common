using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// Blazor Server <see cref="ITokenRefresher"/> while the same-origin proxy is enabled. The default
/// browser refresher reads the access token out of <c>/auth/session/token</c> through script, which
/// would put the credential in the page; this one asks <c>/auth/session/handoff</c> for a protected
/// handoff instead and opens it here, on the server, so the token only ever exists in circuit memory.
/// Registered by <c>AddCommonSameOriginApiProxy</c>.
/// </summary>
internal sealed class HandoffTokenRefresher(IJSRuntime jsRuntime, SessionHandoffProtector protector) : ISessionAwareTokenRefresher
{
    public async Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) =>
        (await TryAcquireAccessTokenAsync(cancellationToken)).AccessToken;

    public async Task<TokenAcquisition> TryAcquireAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The script answers null only for the endpoint's 401 and throws for any other failure.
            var handoff = await jsRuntime.InvokeAsync<string?>("mmcaAuthHandoff.getToken", cancellationToken);
            if (handoff is null)
            {
                return TokenAcquisition.NoSession;
            }

            // A handoff this host cannot open (expired, or a key it does not hold) says nothing about
            // the session itself.
            return protector.UnprotectAccessToken(handoff) is { Length: > 0 } accessToken
                ? TokenAcquisition.Acquired(accessToken)
                : TokenAcquisition.Unavailable;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSDisconnectedException or JSException or OperationCanceledException)
        {
            // JS interop unavailable (SSR prerender / disconnected circuit), a cancelled call, or the
            // endpoint failing transiently: the server-side cookie path handles the interop-less phases.
            return TokenAcquisition.Unavailable;
        }
    }
}

/// <summary>
/// Blazor Server <see cref="ISessionCookieSync"/> while the same-origin proxy is enabled. A sign-in on
/// the circuit happens server-to-server, so the token pair is already in this process; it reaches the
/// HttpOnly cookies as a protected handoff posted by script to <c>/auth/session-cookie/handoff</c>,
/// never as plaintext the page could read. Clearing reuses the existing cookie DELETE. Registered by
/// <c>AddCommonSameOriginApiProxy</c>.
/// </summary>
internal sealed class HandoffSessionCookieSync(IJSRuntime jsRuntime, SessionHandoffProtector protector) : ISessionCookieSync
{
    public async Task<bool> SyncAsync(string accessToken, string refreshToken)
    {
        try
        {
            return await jsRuntime.InvokeAsync<bool>("mmcaAuthHandoff.setCookie", protector.ProtectTokenPair(accessToken, refreshToken));
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            return false;
        }
    }

    public async Task<bool> ClearAsync()
    {
        try
        {
            return await jsRuntime.InvokeAsync<bool>("mmcaAuthCookie.clear");
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            return false;
        }
    }

    private static bool IsInteropUnavailable(Exception ex) =>
        ex is InvalidOperationException or JSDisconnectedException or JSException or OperationCanceledException;
}
