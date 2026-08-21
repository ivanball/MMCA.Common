using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Middleware;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Builds the ordered list of named steps that <c>UseCommonMiddlewarePipeline</c> applies to a
/// <see cref="WebApplication"/>. <see cref="CreateDefault"/> seeds the framework pipeline; a host
/// then inserts, replaces, or removes steps by name and <see cref="Build"/> re-checks the
/// load-bearing adjacencies, so a customized pipeline fails at startup rather than misordering
/// silently.
/// </summary>
public sealed class MiddlewarePipelineBuilder
{
    private readonly List<MiddlewarePipelineStep> _steps;

    private MiddlewarePipelineBuilder(List<MiddlewarePipelineStep> steps) => _steps = steps;

    /// <summary>
    /// The names of the steps currently in the pipeline, in the order they will be applied.
    /// </summary>
    public IReadOnlyList<string> StepNames => [.. _steps.Select(step => step.Name)];

    /// <summary>
    /// Creates a builder seeded with the framework's default edge pipeline, in the order documented
    /// on <see cref="MiddlewarePipelineStepNames"/>.
    /// </summary>
    /// <returns>A builder holding the default steps.</returns>
    public static MiddlewarePipelineBuilder CreateDefault() =>
        new(
        [
            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.ExceptionHandler,
                static app => app.UseExceptionHandler()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.CorrelationId,
                static app => app.UseMiddleware<CorrelationIdMiddleware>()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.RequestLocalization,
                // Set CurrentUICulture for the request (ADR-027) so edge error localization and any
                // culture-aware formatting run under the caller's culture. The UI forwards the active
                // culture as Accept-Language (the default providers include that header + the cookie).
                static app => app.UseCommonRequestLocalization()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.PreForwardedCapture,
                // Capture the actual transport scheme + host before UseForwardedHeaders rewrites
                // Request.Scheme and Request.Host from the X-Forwarded-* headers. The OIDC discovery
                // endpoint needs the original values for jwks_uri: internal services fetch JWKS
                // over cleartext HTTP using the Aspire-resolved DNS name, but envoy/DCP forwards
                // X-Forwarded-Proto: https and X-Forwarded-Host pointing at the canonical
                // launchSettings URL (e.g. localhost:56003) which the caller cannot reach.
                static app => app.Use(static (context, next) =>
                {
                    context.Items[WebApplicationExtensions.PreForwardedSchemeKey] = context.Request.Scheme;
                    context.Items[WebApplicationExtensions.PreForwardedHostKey] = context.Request.Host.Value;
                    return next(context);
                })),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.ForwardedHeaders,
                static app =>
                {
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
                }),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.HttpsRedirection,
                // HTTPS redirect runs for browser/REST traffic only. gRPC clients use HTTP/2
                // cleartext (h2c) on the HTTP endpoint of extracted services: Aspire's project-
                // resource service discovery doesn't reliably expose an https key, so the resolver
                // hands out the http URL. Issuing a 307 redirect on those requests breaks the gRPC
                // call (the client retries against HTTPS, which then has its own issues). Skip
                // HTTPS redirect for any request whose Content-Type starts with "application/grpc".
                static app => app.UseWhen(
                    ctx => !(ctx.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) ?? false),
                    builder => builder.UseHttpsRedirection())),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.ResponseCompression,
                static app => app.UseResponseCompression()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.Routing,
                static app => app.UseRouting()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.Cors,
                static app => app.UseCors(app.Environment.IsDevelopment()
                    ? WebApplicationBuilderExtensions.CorsPolicyAllowAll
                    : WebApplicationBuilderExtensions.CorsPolicyAllowSpecificOrigins)),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.Authentication,
                static app => app.UseAuthentication()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.TenantResolution,
                // Immediately after authentication, and that order is load-bearing: the claim strategy
                // reads HttpContext.User, which carries the token's claims only once authentication has
                // run. Registered unconditionally and inert unless the host called AddMultiTenancy and
                // set Tenancy:Enabled (the SoftDeletedUserMiddleware precedent).
                static app => app.UseMiddleware<TenantResolutionMiddleware>()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.RateLimiting,
                // Rate limiting runs AFTER authentication on purpose (ADR-019): GlobalRateLimitPartition
                // partitions by the authenticated principal and routes anonymous traffic down a NoLimiter
                // branch, so HttpContext.User must already be populated here, otherwise every request
                // looks anonymous and the per-user cap never engages.
                static app => app.UseRateLimiter()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.SoftDeletedUserFilter,
                static app => app.UseMiddleware<SoftDeletedUserMiddleware>()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.Authorization,
                static app => app.UseAuthorization()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.OutputCache,
                static app => app.UseOutputCache()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.JwksEndpoint,
                // Always-mapped JWKS + OIDC discovery endpoints. Returns an empty key set
                // (JWKS) or 404 (OIDC discovery) when the Identity service's RSA publishing
                // is not configured, so non-Identity services incur no behavior change.
                // Identity services flip JwksSettings.Enabled = true and provide RsaPublicKeyPem
                // to publish their signing key for downstream services to fetch via AddForwardedJwtBearer.
                static app => app.MapJwksEndpoint()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.OidcDiscoveryEndpoint,
                static app => app.MapOidcDiscoveryEndpoint()),

            new MiddlewarePipelineStep(
                MiddlewarePipelineStepNames.Controllers,
                static app => app.MapControllers()),
        ]);

    /// <summary>Inserts <paramref name="step"/> immediately before the step named <paramref name="anchor"/>.</summary>
    /// <param name="anchor">The name of the existing step to insert before.</param>
    /// <param name="step">The step to insert.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// No step is named <paramref name="anchor"/>, or a step already carries
    /// <paramref name="step"/>'s name.
    /// </exception>
    public MiddlewarePipelineBuilder InsertBefore(string anchor, MiddlewarePipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var index = RequireIndexOf(anchor, nameof(anchor));
        RequireUniqueName(step.Name, nameof(step));
        _steps.Insert(index, step);
        return this;
    }

    /// <summary>Inserts <paramref name="step"/> immediately after the step named <paramref name="anchor"/>.</summary>
    /// <param name="anchor">The name of the existing step to insert after.</param>
    /// <param name="step">The step to insert.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// No step is named <paramref name="anchor"/>, or a step already carries
    /// <paramref name="step"/>'s name.
    /// </exception>
    public MiddlewarePipelineBuilder InsertAfter(string anchor, MiddlewarePipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var index = RequireIndexOf(anchor, nameof(anchor));
        RequireUniqueName(step.Name, nameof(step));
        _steps.Insert(index + 1, step);
        return this;
    }

    /// <summary>
    /// Replaces the step named <paramref name="name"/> with <paramref name="step"/>, keeping its
    /// position. The replacement may carry a different name.
    /// </summary>
    /// <param name="name">The name of the existing step to replace.</param>
    /// <param name="step">The replacement step.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// No step is named <paramref name="name"/>, or another step already carries
    /// <paramref name="step"/>'s name.
    /// </exception>
    public MiddlewarePipelineBuilder Replace(string name, MiddlewarePipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var index = RequireIndexOf(name, nameof(name));

        if (_steps.Exists(existing => !string.Equals(existing.Name, name, StringComparison.Ordinal)
            && string.Equals(existing.Name, step.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"A middleware pipeline step named '{step.Name}' already exists. Step names must be unique.",
                nameof(step));
        }

        _steps[index] = step;
        return this;
    }

    /// <summary>Removes the step named <paramref name="name"/>.</summary>
    /// <param name="name">The name of the step to remove.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">No step is named <paramref name="name"/>.</exception>
    public MiddlewarePipelineBuilder Remove(string name)
    {
        var index = RequireIndexOf(name, nameof(name));
        _steps.RemoveAt(index);
        return this;
    }

    /// <summary>
    /// Validates the load-bearing adjacencies and returns the ordered steps. An invariant binds only
    /// when both of the steps it names are still present, so a host that drops a whole capability
    /// (both members of a pair) stays legal. The invariants are:
    /// <list type="bullet">
    /// <item><description>
    /// <c>PreForwardedCapture</c> runs immediately before <c>ForwardedHeaders</c>: the captured
    /// pre-forwarded scheme and host are only faithful if nothing rewrites the request between the
    /// capture and the <c>X-Forwarded-*</c> rewrite.
    /// </description></item>
    /// <item><description>
    /// <c>Authentication</c> runs immediately before <c>TenantResolution</c>: the tenant claim
    /// strategy reads <c>HttpContext.User</c>.
    /// </description></item>
    /// <item><description>
    /// <c>Authentication</c> runs before <c>RateLimiting</c> (ADR-019): the global partition keys on
    /// the authenticated principal, so an unauthenticated pipeline sees every request as anonymous.
    /// </description></item>
    /// <item><description>
    /// <c>ForwardedHeaders</c> runs before <c>HttpsRedirection</c>: the redirect decision must see
    /// the proxy-reported scheme, not the internal one.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <returns>The validated steps, in the order they are to be applied.</returns>
    /// <exception cref="InvalidOperationException">An invariant is violated; the message names it.</exception>
    public IReadOnlyList<MiddlewarePipelineStep> Build()
    {
        RequireImmediatelyBefore(
            MiddlewarePipelineStepNames.PreForwardedCapture,
            MiddlewarePipelineStepNames.ForwardedHeaders,
            "the captured pre-forwarded scheme and host are only faithful if nothing rewrites the request between the capture and the X-Forwarded-* rewrite");

        RequireImmediatelyBefore(
            MiddlewarePipelineStepNames.Authentication,
            MiddlewarePipelineStepNames.TenantResolution,
            "the tenant claim strategy reads HttpContext.User, which carries the token's claims only once authentication has run");

        RequirePrecedes(
            MiddlewarePipelineStepNames.Authentication,
            MiddlewarePipelineStepNames.RateLimiting,
            "GlobalRateLimitPartition keys on the authenticated principal (ADR-019); without authentication first, every request looks anonymous and the per-user cap never engages");

        RequirePrecedes(
            MiddlewarePipelineStepNames.ForwardedHeaders,
            MiddlewarePipelineStepNames.HttpsRedirection,
            "the redirect decision must see the proxy-reported scheme from X-Forwarded-Proto, not the internal transport scheme");

        return [.. _steps];
    }

    private int IndexOf(string name) =>
        _steps.FindIndex(step => string.Equals(step.Name, name, StringComparison.Ordinal));

    private int RequireIndexOf(string name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A middleware pipeline step name is required.", parameterName);
        }

        var index = IndexOf(name);

        if (index < 0)
        {
            throw new ArgumentException(
                $"No middleware pipeline step named '{name}' exists. Known steps: {string.Join(", ", StepNames)}.",
                parameterName);
        }

        return index;
    }

    private void RequireUniqueName(string name, string parameterName)
    {
        if (IndexOf(name) >= 0)
        {
            throw new ArgumentException(
                $"A middleware pipeline step named '{name}' already exists. Step names must be unique.",
                parameterName);
        }
    }

    private void RequireImmediatelyBefore(string first, string second, string rationale)
    {
        var firstIndex = IndexOf(first);
        var secondIndex = IndexOf(second);

        // The invariant binds only when both steps are present: removing a whole capability is legal.
        if (firstIndex < 0 || secondIndex < 0 || secondIndex == firstIndex + 1)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Middleware pipeline invariant violated: '{first}' must run immediately before '{second}' ({rationale}). Current order: {string.Join(" -> ", StepNames)}.");
    }

    private void RequirePrecedes(string first, string second, string rationale)
    {
        var firstIndex = IndexOf(first);
        var secondIndex = IndexOf(second);

        // The invariant binds only when both steps are present: removing a whole capability is legal.
        if (firstIndex < 0 || secondIndex < 0 || firstIndex < secondIndex)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Middleware pipeline invariant violated: '{first}' must run before '{second}' ({rationale}). Current order: {string.Join(" -> ", StepNames)}.");
    }
}
