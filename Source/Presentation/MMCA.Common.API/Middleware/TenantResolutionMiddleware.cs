using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Tenancy;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Resolves the tenant for the current request and publishes it on the scoped
/// <see cref="ITenantContext"/>, which the persistence layer's query filter, save interceptor and
/// per-tenant database routing all read from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wired unconditionally, inert by default</b> (the <c>SoftDeletedUserMiddleware</c> precedent).
/// <c>IOptions&lt;TenancySettings&gt;</c> resolves to defaults in a host that never called
/// <c>AddMultiTenancy</c>, and those defaults have <c>Enabled</c> false, so the middleware passes
/// every request straight through.
/// </para>
/// <para>
/// <b>It runs immediately after <c>UseAuthentication</c></b>, and that position is load-bearing:
/// the claim strategy reads <c>HttpContext.User</c>, which does not carry the token's claims until
/// authentication has run. Running it earlier would silently demote every request to the header
/// strategy.
/// </para>
/// <para>
/// <b>It fails closed.</b> With <c>Tenancy:RequireTenant</c> (the default) a request that resolves
/// no tenant is answered with <c>400 Bad Request</c> rather than allowed through unscoped: an
/// unscoped request reads across every tenant, which is the exact outcome tenancy exists to prevent.
/// Health, liveness and discovery paths are excluded, since they must answer before any tenant
/// exists.
/// </para>
/// </remarks>
/// <param name="next">The next middleware in the pipeline.</param>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>The problem type reported when a required tenant could not be resolved.</summary>
    internal const string UnresolvedTenantTitle = "Tenant not resolved";

    /// <summary>
    /// Resolves the tenant per the configured strategy order and either publishes it, passes the
    /// request through, or rejects it.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="tenantContext">The scoped tenant context to populate.</param>
    /// <param name="tenancyOptions">The bound tenancy settings.</param>
    /// <param name="problemDetailsService">The service used to write the RFC 9457 rejection.</param>
    /// <returns>A task representing the middleware execution.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        IOptions<TenancySettings> tenancyOptions,
        IProblemDetailsService problemDetailsService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(tenancyOptions);

        var settings = tenancyOptions.Value;

        if (!settings.Enabled || IsExcluded(context, settings))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (Resolve(context, settings) is { } tenantId)
        {
            tenantContext.SetTenant(tenantId);
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!settings.RequireTenant)
        {
            // Explicitly opted out of failing closed: the request runs as a system caller and sees
            // every tenant's rows, which is only ever right behind an internal boundary.
            await next(context).ConfigureAwait(false);
            return;
        }

        await RejectAsync(context, settings, problemDetailsService).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the request path is one of the excluded prefixes (probes and discovery documents).
    /// </summary>
    private static bool IsExcluded(HttpContext context, TenancySettings settings)
    {
        var path = context.Request.Path;
        return settings.EffectiveExcludedPathPrefixes
            .Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tries each configured strategy in order and returns the first non-blank tenant, or
    /// <see langword="null"/> when none yields one.
    /// </summary>
    /// <remarks>
    /// <see cref="TenantResolutionStrategy.Host"/> is skipped rather than guessed at: it is defined
    /// but not implemented, and options validation already refuses to start a host that selected it,
    /// so reaching it here would mean validation was bypassed.
    /// </remarks>
    private static string? Resolve(HttpContext context, TenancySettings settings)
    {
        foreach (var strategy in settings.EffectiveResolutionOrder)
        {
            var candidate = strategy switch
            {
                TenantResolutionStrategy.Claim => context.User?.FindFirst(settings.ClaimType)?.Value,
                TenantResolutionStrategy.Header => context.Request.Headers[settings.HeaderName].FirstOrDefault(),
                TenantResolutionStrategy.Host => null,
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        return null;
    }

    /// <summary>
    /// Answers an unresolvable request with <c>400 Bad Request</c> as RFC 9457 problem details,
    /// naming the claim and header that were looked at so the caller can fix the request.
    /// </summary>
    private static async Task RejectAsync(
        HttpContext context,
        TenancySettings settings,
        IProblemDetailsService problemDetailsService)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problem = new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = UnresolvedTenantTitle,
                Detail = "This request carries no tenant. Supply it as the \""
                    + settings.ClaimType
                    + "\" claim or the \""
                    + settings.HeaderName
                    + "\" header.",
            },
        };

        await problemDetailsService.TryWriteAsync(problem).ConfigureAwait(false);
    }
}
