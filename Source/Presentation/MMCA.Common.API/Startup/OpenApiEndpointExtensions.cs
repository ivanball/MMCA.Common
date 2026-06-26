using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Maps the OpenAPI document endpoint for MMCA service hosts (rubric §9). Wraps ASP.NET Core's
/// built-in <c>MapOpenApi()</c> so every service serves a machine-readable contract at
/// <c>/openapi/v1.json</c> the same way; pair it with <c>AddCommonOpenApi()</c> (see
/// <see cref="WebApplicationBuilderExtensions"/>). The document is the source of truth for the API
/// surface and is intended to be guarded by a contract-snapshot test in the consumer integration
/// tiers so it cannot drift silently (the framework deliberately does not duplicate that gate — the
/// API surface lives in the consumer hosts). Mapped <b>outside Production only</b> — these are internal
/// services reached through the Gateway (which does not route the endpoint), so the spec is a dev/CI
/// artifact, not a public production surface. <see cref="MapCommonScalarUi"/> optionally renders it.
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

        /// <summary>
        /// Maps the Scalar interactive API-reference UI (<c>/scalar/{documentName}</c>) <b>outside
        /// Production</b> — an <b>opt-in</b> developer convenience for browsing the generated OpenAPI
        /// document. Requires <c>AddCommonOpenApi()</c> + <c>MapCommonOpenApi()</c>. No-op in Production.
        /// Internal services fronted by the Gateway typically do not call this (they expose only the JSON
        /// in dev/CI); it exists for hosts run standalone where a rendered reference helps. Assets are
        /// served by the bundled <c>Scalar.AspNetCore</c> package (no external CDN).
        /// </summary>
        public WebApplication MapCommonScalarUi()
        {
            if (!app.Environment.IsProduction())
            {
                app.MapScalarApiReference();
            }

            return app;
        }
    }
}
