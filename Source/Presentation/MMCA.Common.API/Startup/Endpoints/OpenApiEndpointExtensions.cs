using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace MMCA.Common.API.Startup.Endpoints;

/// <summary>
/// Maps the OpenAPI document endpoint for MMCA service hosts (rubric §9). Wraps ASP.NET Core's
/// built-in <c>MapOpenApi()</c> so every service serves a machine-readable contract at
/// <c>/openapi/v1.json</c> the same way; pair it with the host's own <c>services.AddOpenApi()</c>
/// plus <c>AddCommonOpenApi()</c> (see <see cref="WebApplicationBuilderExtensions"/>). The document
/// is the source of truth for the API surface, and it is guarded at two levels so it cannot drift
/// silently. The framework-owned part of the generated document (the unbound-route-token backfill,
/// the generated <c>ProblemDetails</c> error schema) is diffed against a committed baseline in-repo
/// by <c>OpenApiBaselineTests</c> (Tests/Presentation/MMCA.Common.API.Tests/OpenApi), which fails on
/// any change until the baseline is regenerated deliberately in the same pull request. Each
/// consumer's concrete API surface stays the concern of that host's committed document and its
/// contract-snapshot tests. Mapped <b>outside Production only</b>: these are internal services
/// reached through the Gateway (which does not route the endpoint), so the spec is a dev/CI
/// artifact, not a public production surface. <see cref="MapCommonScalarUi"/> optionally renders it.
/// </summary>
public static class OpenApiEndpointExtensions
{
    /// <summary>The document name plain <c>AddOpenApi()</c> registers and this endpoint serves.</summary>
    private const string DefaultDocumentName = "v1";

    extension(WebApplication app)
    {
        /// <summary>
        /// Maps the OpenAPI document endpoint (<c>/openapi/{documentName}.json</c>) <b>outside
        /// Production</b>, matching the convention that these internal service specs are dev/CI
        /// artifacts, not a public production surface. No-op in Production.
        /// <para>
        /// Requires the host to have registered the document itself with <c>services.AddOpenApi()</c>
        /// (next to <c>AddCommonOpenApi()</c>, which registers none). Outside Production this throws
        /// <see cref="InvalidOperationException"/> when no <c>v1</c> document is registered, so a host
        /// that calls only the framework method fails at startup (and at build-time document
        /// generation) instead of serving no document.
        /// </para>
        /// <para>
        /// The document is mapped <b>anonymous</b>. The framework's fallback authorization policy
        /// (SEC-Common-16, <c>AddAuthorizationPolicies</c>) gates every endpoint that states nothing,
        /// and <c>/openapi</c> is not one of its exempt prefixes, so an undeclared mapping answers 401
        /// to the contract-snapshot tests and API tooling. The contract is the same shape every caller
        /// already reads from the controllers it describes, it must answer before a client holds a
        /// token, and it is never mapped in Production.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Outside Production, when the host did not register the <c>v1</c> document with
        /// <c>services.AddOpenApi()</c>.
        /// </exception>
        [SuppressMessage(
            "Usage",
            "AV0030:Missing WithDocumentPerVersion",
            Justification = "False positive here: AV0030 fires because AddCommonApiVersioning's AddApiExplorer call is in this assembly, but no versioned OpenAPI registration exists (Asp.Versioning.OpenApi is not referenced, so WithDocumentPerVersion is not even available). The host registers the plain v1 document with AddOpenApi() so the OpenAPI XML-comment generator attaches its summaries.")]
        public WebApplication MapCommonOpenApi()
        {
            if (!app.Environment.IsProduction())
            {
                IServiceProviderIsKeyedService? keyed = app.Services.GetService<IServiceProviderIsKeyedService>();
                if (keyed?.IsKeyedService(typeof(IOpenApiDocumentProvider), DefaultDocumentName) != true)
                {
                    throw new InvalidOperationException(
                        "MapCommonOpenApi() found no '" + DefaultDocumentName + "' OpenAPI document. Call "
                        + "services.AddOpenApi() in the host project itself (it is the call the OpenAPI "
                        + "XML-comment generator intercepts to attach the host's summaries) next to "
                        + "services.AddCommonOpenApi(), which only configures the registered documents.");
                }

                app.MapOpenApi().AllowAnonymous();
            }

            return app;
        }

        /// <summary>
        /// Maps the Scalar interactive API-reference UI (<c>/scalar/{documentName}</c>) <b>outside
        /// Production</b>: an <b>opt-in</b> developer convenience for browsing the generated OpenAPI
        /// document. Requires <c>services.AddOpenApi()</c> + <c>AddCommonOpenApi()</c> +
        /// <c>MapCommonOpenApi()</c>. No-op in Production.
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
