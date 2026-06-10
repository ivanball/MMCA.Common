using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Runs <b>before</b> <c>UseAuthentication</c> on full-page navigations: if the access cookie's JWT has
/// expired but the refresh cookie is still valid, it refreshes server-side and stashes the fresh access
/// token on <see cref="HttpContext.Items"/> so SSR <c>[Authorize]</c> survives instead of bouncing to
/// <c>/login</c>. Gated to GET + <c>Accept: text/html</c> (navigations only) so it never fires on static
/// assets or API/XHR calls, and single-flighted by the refresher so it can't double-rotate the token.
/// </summary>
public sealed class CookieSessionRefreshMiddleware(RequestDelegate next, ICookieSessionRefresher refresher)
{
    /// <summary>Attempts a validate-or-refresh for qualifying navigations, then invokes the pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ShouldAttempt(context.Request))
        {
            await refresher.GetOrRefreshAsync(context, context.RequestAborted).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool ShouldAttempt(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        request.Headers.Accept.Any(static accept =>
            accept is not null && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Pipeline extension for <see cref="CookieSessionRefreshMiddleware"/>.</summary>
public static class CookieSessionRefreshMiddlewareExtensions
{
    /// <summary>
    /// Adds the SSR validate-or-refresh middleware. Register it immediately <b>before</b>
    /// <c>UseAuthentication()</c> on the Blazor Server (UI.Web) host.
    /// </summary>
    public static IApplicationBuilder UseCookieSessionRefresh(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CookieSessionRefreshMiddleware>();
    }
}
