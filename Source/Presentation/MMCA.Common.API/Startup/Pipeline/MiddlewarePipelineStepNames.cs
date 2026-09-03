namespace MMCA.Common.API.Startup.Pipeline;

/// <summary>
/// The well-known names of the steps that make up the shared HTTP edge pipeline built by
/// <see cref="MiddlewarePipelineBuilder.CreateDefault"/> and applied by
/// <c>UseCommonMiddlewarePipeline</c>. A host customizing the pipeline addresses steps by these
/// names, so they are part of the public contract: renaming one is a breaking change.
/// <para>
/// The declaration order below is the runtime order of the default pipeline. Several adjacencies
/// are load-bearing and are re-checked by <see cref="MiddlewarePipelineBuilder.Build"/>; see the
/// invariant list documented there.
/// </para>
/// </summary>
public static class MiddlewarePipelineStepNames
{
    /// <summary>The framework exception handler (<c>UseExceptionHandler</c>), outermost step.</summary>
    public const string ExceptionHandler = "ExceptionHandler";

    /// <summary>Correlation-id capture and propagation (<c>CorrelationIdMiddleware</c>).</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>Per-request culture resolution (<c>UseCommonRequestLocalization</c>, ADR-027).</summary>
    public const string RequestLocalization = "RequestLocalization";

    /// <summary>
    /// Captures the transport scheme and host as the connection saw them, before the forwarded
    /// headers rewrite them. Must run immediately before <see cref="ForwardedHeaders"/>.
    /// </summary>
    public const string PreForwardedCapture = "PreForwardedCapture";

    /// <summary>Applies <c>X-Forwarded-For/Proto/Host</c> (<c>UseForwardedHeaders</c>).</summary>
    public const string ForwardedHeaders = "ForwardedHeaders";

    /// <summary>HTTPS redirection for non-gRPC traffic (<c>UseHttpsRedirection</c> under a <c>UseWhen</c>).</summary>
    public const string HttpsRedirection = "HttpsRedirection";

    /// <summary>Response compression (<c>UseResponseCompression</c>).</summary>
    public const string ResponseCompression = "ResponseCompression";

    /// <summary>Endpoint routing (<c>UseRouting</c>).</summary>
    public const string Routing = "Routing";

    /// <summary>CORS policy selection (<c>UseCors</c>); the policy depends on the environment.</summary>
    public const string Cors = "Cors";

    /// <summary>Authentication (<c>UseAuthentication</c>); populates <c>HttpContext.User</c>.</summary>
    public const string Authentication = "Authentication";

    /// <summary>
    /// Tenant resolution (<c>TenantResolutionMiddleware</c>). Must run immediately after
    /// <see cref="Authentication"/>: the claim strategy reads <c>HttpContext.User</c>.
    /// </summary>
    public const string TenantResolution = "TenantResolution";

    /// <summary>Global rate limiter (<c>UseRateLimiter</c>); must run after <see cref="Authentication"/> (ADR-019).</summary>
    public const string RateLimiting = "RateLimiting";

    /// <summary>Rejects requests from soft-deleted users (<c>SoftDeletedUserMiddleware</c>).</summary>
    public const string SoftDeletedUserFilter = "SoftDeletedUserFilter";

    /// <summary>Authorization (<c>UseAuthorization</c>).</summary>
    public const string Authorization = "Authorization";

    /// <summary>Output caching (<c>UseOutputCache</c>).</summary>
    public const string OutputCache = "OutputCache";

    /// <summary>Maps <c>/.well-known/jwks.json</c> (<c>MapJwksEndpoint</c>).</summary>
    public const string JwksEndpoint = "JwksEndpoint";

    /// <summary>Maps <c>/.well-known/openid-configuration</c> (<c>MapOidcDiscoveryEndpoint</c>).</summary>
    public const string OidcDiscoveryEndpoint = "OidcDiscoveryEndpoint";

    /// <summary>Maps the attribute-routed controllers (<c>MapControllers</c>), innermost step.</summary>
    public const string Controllers = "Controllers";
}
