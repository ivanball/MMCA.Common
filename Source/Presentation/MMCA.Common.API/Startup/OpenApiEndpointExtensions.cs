using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Maps the OpenAPI document endpoint for MMCA service hosts (rubric §9). Wraps ASP.NET Core's
/// built-in <c>MapOpenApi()</c> so every service serves a machine-readable contract at
/// <c>/openapi/v1.json</c> the same way; pair it with <c>AddCommonOpenApi()</c> (see
/// <see cref="WebApplicationBuilderExtensions"/>). The document is the source of truth for the API
/// surface and is intended to be guarded by a contract-snapshot test in the consumer integration
/// tiers so it cannot drift silently. Mapped <b>outside Production only</b> — these are internal
/// services reached through the Gateway (which does not route the endpoint), so the spec is a dev/CI
/// artifact, not a public production surface.
/// </summary>
public static class OpenApiEndpointExtensions
{
    extension(WebApplication app)
    {
        /// <summary>
        /// Maps the OpenAPI document endpoint (<c>/openapi/{documentName}.json</c>) <b>outside
        /// Production</b> — matching the convention that these internal service specs are dev/CI
        /// artifacts, not a public production surface. Requires <c>AddCommonOpenApi()</c> to have been
        /// called during service registration. No-op in Production.
        /// </summary>
        public WebApplication MapCommonOpenApi()
        {
            if (!app.Environment.IsProduction())
            {
                app.MapOpenApi();
            }

            return app;
        }
    }
}
