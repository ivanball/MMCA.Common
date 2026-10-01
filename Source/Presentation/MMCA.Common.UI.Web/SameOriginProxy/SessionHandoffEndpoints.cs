using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMCA.Common.API.SessionCookies;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// The two same-origin endpoints the Blazor Server circuit uses through script while the proxy is
/// enabled (<see cref="HandoffTokenRefresher"/> and <see cref="HandoffSessionCookieSync"/>). Both trade
/// only <see cref="SessionHandoffProtector"/> ciphertext with the page, both are anonymous (they
/// authenticate through the session cookies or the handoff itself) and both require the proxy's CSRF
/// header.
/// </summary>
internal static class SessionHandoffEndpoints
{
    internal const string TokenHandoffPath = "/auth/session/handoff";
    internal const string CookieHandoffPath = "/auth/session-cookie/handoff";

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        // Validate-or-refresh from the cookies, answered with a protected access token for the circuit.
        endpoints.MapPost(TokenHandoffPath, async (
                HttpContext httpContext, ICookieSessionRefresher refresher, SessionHandoffProtector protector, CancellationToken cancellationToken) =>
            {
                if (!SameOriginApiProxyEndpoint.HasCsrfHeader(httpContext.Request))
                {
                    return Results.Json(new { error = "csrf_header_required" }, statusCode: StatusCodes.Status403Forbidden);
                }

                var session = await refresher.GetOrRefreshAsync(httpContext, cancellationToken).ConfigureAwait(false);
                return session is null
                    ? Results.Json(new { error = "no_session" }, statusCode: StatusCodes.Status401Unauthorized)
                    : Results.Json(new HandoffBody(protector.ProtectAccessToken(session.Value.AccessToken)));
            })
            .ExcludeFromDescription()
            .AllowAnonymous()
            .DisableAntiforgery();

        // Seeds the HttpOnly cookies from a protected token pair the circuit minted after a sign-in.
        endpoints.MapPost(CookieHandoffPath, (
                HandoffBody body, HttpContext httpContext, SessionHandoffProtector protector, ISessionCookieStore cookieStore) =>
            {
                if (!SameOriginApiProxyEndpoint.HasCsrfHeader(httpContext.Request))
                {
                    return Results.Json(new { error = "csrf_header_required" }, statusCode: StatusCodes.Status403Forbidden);
                }

                var pair = protector.UnprotectTokenPair(body.Handoff);
                if (pair is null)
                {
                    return Results.Json(new { error = "invalid_handoff" }, statusCode: StatusCodes.Status400BadRequest);
                }

                cookieStore.Write(httpContext, pair.Value.AccessToken, pair.Value.RefreshToken);
                return Results.NoContent();
            })
            .ExcludeFromDescription()
            .AllowAnonymous()
            .DisableAntiforgery();
    }

    /// <summary>The JSON body both endpoints exchange: <c>{ "handoff": "..." }</c>.</summary>
    internal sealed record HandoffBody(string? Handoff);
}
