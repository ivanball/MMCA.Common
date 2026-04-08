using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Middleware;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Shared middleware pipeline configuration used by all downstream MMCA applications.
/// Ensures consistent middleware ordering across projects.
/// </summary>
public static class WebApplicationExtensions
{
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
            var forwardedHeadersOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
            };

            // Cloud reverse proxies (Azure Container Apps, AWS ALB, etc.) use internal
            // IPs that are not in the default KnownProxies/KnownNetworks allow-lists.
            // Clear them so forwarded headers are trusted regardless of proxy IP.
            forwardedHeadersOptions.KnownProxies.Clear();
            forwardedHeadersOptions.KnownIPNetworks.Clear();
            app.UseForwardedHeaders(forwardedHeadersOptions);
            app.UseHttpsRedirection();
            app.UseResponseCompression();
            app.UseRouting();
            app.UseCors(app.Environment.IsDevelopment()
                ? WebApplicationBuilderExtensions.CorsPolicyAllowAll
                : WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins);
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseMiddleware<SoftDeletedUserMiddleware>();
            app.UseAuthorization();
            app.UseOutputCache();

            // Always-mapped JWKS endpoint. Returns an empty key set when JwksSettings.Enabled
            // is false (the default), so monolith deployments incur no behavior change.
            // Identity services flip JwksSettings.Enabled = true and provide RsaPublicKeyPem
            // to publish their signing key for downstream services to fetch via AddForwardedJwtBearer.
            app.MapJwksEndpoint();

            app.MapControllers();

            return app;
        }
    }
}
