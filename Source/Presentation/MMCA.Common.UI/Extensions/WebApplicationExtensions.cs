using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MMCA.Common.UI.Extensions;

/// <summary>
/// Middleware extensions used by Blazor Server / WASM hybrid hosts.
/// </summary>
public static class WebApplicationExtensions
{
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Adds middleware that emits <c>Cache-Control: no-store</c> on HTML responses
        /// to authenticated users. This opts authenticated pages out of the browser's
        /// back-forward cache so a logged-out user pressing back never sees the
        /// previous logged-in HTML, and so authenticated back-navigation always
        /// triggers a fresh server render instead of restoring stale content.
        /// </summary>
        /// <remarks>
        /// Public (anonymous) pages stay bfcache-eligible because the check is
        /// gated on <c>HttpContext.User.Identity.IsAuthenticated</c>. Register this
        /// <strong>before</strong> <c>MapRazorComponents</c> so it wraps every page response.
        /// </remarks>
        public IApplicationBuilder UseAuthenticatedNoStore()
        {
            app.Use((context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    if (context.User.Identity?.IsAuthenticated is true &&
                        context.Response.ContentType is { } contentType &&
                        contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                        context.Response.Headers["Pragma"] = "no-cache";
                    }
                    return Task.CompletedTask;
                });

                return next();
            });

            return app;
        }
    }
}
