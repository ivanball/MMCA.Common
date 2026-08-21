using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Shared middleware pipeline configuration used by all downstream MMCA applications.
/// Ensures consistent middleware ordering across projects.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Key stored in <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> before
    /// <see cref="ForwardedHeadersExtensions.UseForwardedHeaders(IApplicationBuilder, ForwardedHeadersOptions)"/>
    /// runs. Consumed by <see cref="OidcDiscoveryEndpointExtensions"/> to construct
    /// <c>jwks_uri</c> using the actual transport scheme, not the forwarded one.
    /// </summary>
    internal const string PreForwardedSchemeKey = "PreForwardedScheme";

    /// <summary>
    /// Companion to <see cref="PreForwardedSchemeKey"/>. Stores the request Host header as
    /// the connection actually saw it, before <c>UseForwardedHeaders</c> rewrites Request.Host
    /// from <c>X-Forwarded-Host</c>. Aspire/DCP injects an <c>X-Forwarded-Host</c> pointing at
    /// the canonical launchSettings URL (e.g. <c>localhost:56003</c>) which is unreachable to
    /// internal callers — they reached the service via the Aspire-allocated DNS name. The OIDC
    /// discovery endpoint needs the original host so <c>jwks_uri</c> is reachable from the
    /// same caller that just fetched the discovery document.
    /// </summary>
    internal const string PreForwardedHostKey = "PreForwardedHost";

    extension(WebApplication app)
    {
        /// <summary>
        /// Configures the standard MMCA middleware pipeline. The order is the contract and it is
        /// data, not prose: the steps are the defaults of
        /// <see cref="MiddlewarePipelineBuilder.CreateDefault"/>, named in application order by
        /// <see cref="MiddlewarePipelineStepNames"/> and frozen by the
        /// <c>MiddlewarePipelineOrderTestsBase</c> fitness function. Use the
        /// <c>UseCommonMiddlewarePipeline(Action&lt;MiddlewarePipelineBuilder&gt;)</c>
        /// overload to customize them.
        /// </summary>
        public WebApplication UseCommonMiddlewarePipeline() => ApplyPipeline(app, configure: null);

        /// <summary>
        /// Configures the standard MMCA middleware pipeline after letting the host adjust it: the
        /// default steps are seeded first, <paramref name="configure"/> may insert, replace, or
        /// remove steps by name, and the load-bearing adjacencies are re-checked before anything is
        /// applied (see <see cref="MiddlewarePipelineBuilder.Build"/>), so a misordered pipeline
        /// fails at startup instead of at the first misrouted request.
        /// </summary>
        /// <param name="configure">Adjusts the seeded default pipeline. Runs before any step is applied.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The customized pipeline violates an invariant.</exception>
        public WebApplication UseCommonMiddlewarePipeline(Action<MiddlewarePipelineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return ApplyPipeline(app, configure);
        }

        /// <summary>
        /// Adds <c>RequestLocalization</c> for the framework's supported cultures
        /// (<see cref="SupportedCultures"/>, ADR-027). Wired into <c>UseCommonMiddlewarePipeline</c>
        /// for REST/gRPC service hosts; Blazor UI hosts call this explicitly before <c>MapRazorComponents</c>
        /// so SSR prerender runs under the right culture. The default providers resolve culture from the
        /// query string, the ASP.NET culture cookie, then the <c>Accept-Language</c> header.
        /// </summary>
        public WebApplication UseCommonRequestLocalization()
        {
            List<string> supported = [.. SupportedCultures.All];

            // Pseudo-localization (ADR-027 §8): a Development-only locale that runtime-transforms every
            // resolved resource string to surface hard-coded strings, truncation, and concatenation.
            // Never offered outside Development, so the pseudo decorator stays inert in production.
            if (app.Environment.IsDevelopment())
            {
                supported.Add(SupportedCultures.PseudoLocale);
            }

            string[] supportedArray = [.. supported];
            var options = new RequestLocalizationOptions()
                .SetDefaultCulture(SupportedCultures.Default)
                .AddSupportedCultures(supportedArray)
                .AddSupportedUICultures(supportedArray);

            app.UseRequestLocalization(options);
            return app;
        }

        /// <summary>
        /// Maps the <c>GET /culture/set?culture={c}&amp;redirectUri={uri}</c> endpoint (ADR-027) that the
        /// culture switcher calls. It writes the standard ASP.NET culture cookie (non-HttpOnly so the WASM
        /// client can read it) and local-redirects back, forcing a full reload so SSR re-renders and the
        /// WASM runtime re-reads the cookie. Anonymous-accessible; only allowlisted cultures are honored.
        /// Map on Blazor UI hosts.
        /// </summary>
        public WebApplication MapCultureEndpoint()
        {
            // Permit the pseudo locale only in Development (ADR-027 §8); it is a developer diagnostic,
            // never a production culture.
            var allowPseudo = app.Environment.IsDevelopment();
            app.MapGet("/culture/set", (string culture, string? redirectUri, HttpContext context) =>
            {
                if (SupportedCultures.IsSupported(culture) || allowPseudo && SupportedCultures.IsPseudoLocale(culture))
                {
#pragma warning disable S3330, S2092 // S3330: non-HttpOnly by design (ADR-027), the WASM client reads this cookie directly on startup (MmcaCultureBootstrap.SetBrowserCultureAsync); S2092: Secure is conditional on the hosting environment (false only in Development), same pattern as SessionCookieJar.BuildOptions
                    context.Response.Cookies.Append(
                        CookieRequestCultureProvider.DefaultCookieName,
                        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                        new CookieOptions
                        {
                            Path = "/",
                            Expires = DateTimeOffset.UtcNow.AddYears(1),
                            IsEssential = true,
                            HttpOnly = false,
                            Secure = !app.Environment.IsDevelopment(),
                            SameSite = SameSiteMode.Lax,
                        });
#pragma warning restore S3330, S2092
                }

                var target = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri;
                return Results.LocalRedirect(target);
            }).AllowAnonymous();

            return app;
        }
    }

    /// <summary>
    /// Seeds the default steps, lets the host adjust them, validates the load-bearing adjacencies,
    /// then applies every step in order. Both <c>UseCommonMiddlewarePipeline</c> overloads route
    /// through here, so the zero-argument path is exactly the validated default pipeline.
    /// </summary>
    private static WebApplication ApplyPipeline(WebApplication app, Action<MiddlewarePipelineBuilder>? configure)
    {
        var builder = MiddlewarePipelineBuilder.CreateDefault();
        configure?.Invoke(builder);

        foreach (var step in builder.Build())
        {
            step.Configure(app);
        }

        return app;
    }
}
