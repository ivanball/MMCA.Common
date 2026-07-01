using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Middleware;
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
        /// Configures the standard MMCA middleware pipeline in the correct order:
        /// exception handling → correlation ID → forwarded headers → HTTPS →
        /// response compression → routing → CORS → rate limiting → auth →
        /// soft-delete user filter → authorization → output cache → controllers.
        /// </summary>
        public WebApplication UseCommonMiddlewarePipeline()
        {
            app.UseExceptionHandler();
            app.UseMiddleware<CorrelationIdMiddleware>();

            // Set CurrentUICulture for the request (ADR-027) so edge error localization and any
            // culture-aware formatting run under the caller's culture. The UI forwards the active
            // culture as Accept-Language (the default providers include that header + the cookie).
            app.UseCommonRequestLocalization();

            var forwardedHeadersOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
            };

            // Cloud reverse proxies (Azure Container Apps, AWS ALB, etc.) use internal
            // IPs that are not in the default KnownProxies/KnownNetworks allow-lists.
            // Clear them so forwarded headers are trusted regardless of proxy IP.
            forwardedHeadersOptions.KnownProxies.Clear();
            forwardedHeadersOptions.KnownIPNetworks.Clear();

            // Capture the actual transport scheme + host before UseForwardedHeaders rewrites
            // Request.Scheme and Request.Host from the X-Forwarded-* headers. The OIDC discovery
            // endpoint needs the original values for jwks_uri — internal services fetch JWKS
            // over cleartext HTTP using the Aspire-resolved DNS name, but envoy/DCP forwards
            // X-Forwarded-Proto: https and X-Forwarded-Host pointing at the canonical
            // launchSettings URL (e.g. localhost:56003) which the caller cannot reach.
            app.Use(static (context, next) =>
            {
                context.Items[PreForwardedSchemeKey] = context.Request.Scheme;
                context.Items[PreForwardedHostKey] = context.Request.Host.Value;
                return next(context);
            });

            app.UseForwardedHeaders(forwardedHeadersOptions);

            // HTTPS redirect runs for browser/REST traffic only. gRPC clients use HTTP/2
            // cleartext (h2c) on the HTTP endpoint of extracted services — Aspire's project-
            // resource service discovery doesn't reliably expose an https key, so the resolver
            // hands out the http URL. Issuing a 307 redirect on those requests breaks the gRPC
            // call (the client retries against HTTPS, which then has its own issues). Skip
            // HTTPS redirect for any request whose Content-Type starts with "application/grpc".
            app.UseWhen(
                ctx => !(ctx.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) ?? false),
                builder => builder.UseHttpsRedirection());

            app.UseResponseCompression();
            app.UseRouting();
            app.UseCors(app.Environment.IsDevelopment()
                ? WebApplicationBuilderExtensions.CorsPolicyAllowAll
                : WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins);
            app.UseAuthentication();
            // Rate limiting runs AFTER authentication on purpose (ADR-019): GlobalRateLimitPartition
            // partitions by the authenticated principal and routes anonymous traffic down a NoLimiter
            // branch, so HttpContext.User must already be populated here — otherwise every request
            // looks anonymous and the per-user cap never engages.
            app.UseRateLimiter();
            app.UseMiddleware<SoftDeletedUserMiddleware>();
            app.UseAuthorization();
            app.UseOutputCache();

            // Always-mapped JWKS + OIDC discovery endpoints. Returns an empty key set
            // (JWKS) or 404 (OIDC discovery) when the Identity service's RSA publishing
            // is not configured, so non-Identity services incur no behavior change.
            // Identity services flip JwksSettings.Enabled = true and provide RsaPublicKeyPem
            // to publish their signing key for downstream services to fetch via AddForwardedJwtBearer.
            app.MapJwksEndpoint();
            app.MapOidcDiscoveryEndpoint();

            app.MapControllers();

            return app;
        }

        /// <summary>
        /// Adds <c>RequestLocalization</c> for the framework's supported cultures
        /// (<see cref="SupportedCultures"/>, ADR-027). Wired into <see cref="UseCommonMiddlewarePipeline"/>
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
                    context.Response.Cookies.Append(
                        CookieRequestCultureProvider.DefaultCookieName,
                        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                        new CookieOptions
                        {
                            Path = "/",
                            Expires = DateTimeOffset.UtcNow.AddYears(1),
                            IsEssential = true,
                            HttpOnly = false,
                            SameSite = SameSiteMode.Lax,
                        });
                }

                var target = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri;
                return Results.LocalRedirect(target);
            }).AllowAnonymous();

            return app;
        }
    }
}
