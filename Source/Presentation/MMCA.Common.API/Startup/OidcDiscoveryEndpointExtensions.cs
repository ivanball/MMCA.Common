using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Maps a minimal OpenID Connect discovery document at
/// <c>/.well-known/openid-configuration</c>. The JWT bearer middleware in downstream
/// services fetches this when <c>AddForwardedJwtBearer</c> sets an
/// <see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions.Authority"/>.
/// The response contains just enough for token validation: the <c>issuer</c> and
/// <c>jwks_uri</c> fields.
/// <para>
/// When <c>Jwt:Issuer</c> is not configured (non-Identity services), the endpoint
/// returns <c>404 Not Found</c> — safe because no downstream service points its
/// authority at a non-Identity host.
/// </para>
/// </summary>
public static class OidcDiscoveryEndpointExtensions
{
    /// <summary>
    /// Default OpenID Configuration path per RFC 8615 (well-known URIs).
    /// </summary>
    public const string DefaultOidcDiscoveryPath = "/.well-known/openid-configuration";

    private static readonly string[] ResponseTypesSupported = ["token"];
    private static readonly string[] SubjectTypesSupported = ["public"];
    private static readonly string[] SigningAlgsSupported = ["RS256"];

    /// <summary>
    /// OIDC discovery field names are snake_case per RFC 8414. The default ASP.NET Core
    /// <c>Results.Json</c> serializer uses camelCase (<c>JsonNamingPolicy.CamelCase</c>),
    /// which would transform <c>jwks_uri</c> to <c>"jwksUri"</c> — unrecognised by
    /// <c>OpenIdConnectConfigurationRetriever</c>. Disabling the naming policy preserves
    /// the exact C# property names (which are already in OIDC snake_case).
    /// </summary>
    private static readonly JsonSerializerOptions OidcJsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the OpenID Connect discovery endpoint at <see cref="DefaultOidcDiscoveryPath"/>.
        /// Returns the JWT issuer and JWKS URI so downstream services can discover signing
        /// keys automatically. Anonymous access is required because the middleware fetches
        /// this document before any token is available.
        /// </summary>
        /// <returns>The endpoint route builder for chaining.</returns>
        public IEndpointRouteBuilder MapOidcDiscoveryEndpoint()
        {
            endpoints.MapGet(DefaultOidcDiscoveryPath, (HttpContext context, IConfiguration configuration) =>
            {
                var issuer = configuration["Jwt:Issuer"];
                if (string.IsNullOrWhiteSpace(issuer))
                {
                    return Results.NotFound();
                }

                var jwksUri = $"{context.Request.Scheme}://{context.Request.Host}{JwksEndpointExtensions.DefaultJwksPath}";

                return Results.Json(new
                {
                    issuer,
                    jwks_uri = jwksUri,
                    response_types_supported = ResponseTypesSupported,
                    subject_types_supported = SubjectTypesSupported,
                    id_token_signing_alg_values_supported = SigningAlgsSupported,
                }, OidcJsonOptions);
            }).AllowAnonymous();

            return endpoints;
        }
    }
}
