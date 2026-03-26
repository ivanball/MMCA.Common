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
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });
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
            app.MapControllers();

            return app;
        }
    }
}
