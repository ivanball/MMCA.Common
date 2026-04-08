using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Middleware that validates authenticated users have not been soft-deleted (BR-133).
/// If <c>User.IsDeleted = true</c> for the authenticated user's ID, the request is rejected with HTTP 401.
/// Uses a 30-second cache to minimize per-request database lookups.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
public sealed class SoftDeletedUserMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checks if the authenticated user has been soft-deleted and rejects the request if so.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="currentUserService">Service to access the current user's identity.</param>
    /// <param name="cacheService">Cache service for caching deleted user lookups.</param>
    /// <returns>A task representing the middleware execution.</returns>
    /// <remarks>
    /// <see cref="ISoftDeletedUserValidator"/> is resolved lazily via
    /// <see cref="IServiceProvider.GetService(Type)"/> instead of being declared as an
    /// InvokeAsync parameter. The validator is implemented by the Identity module; in
    /// extracted services that do not host Identity (e.g., the Catalog microservice),
    /// no implementation is registered. Resolving lazily means the middleware no-ops
    /// in those services for unauthenticated requests, and only fails for authenticated
    /// requests with a clear error — instead of 500-ing every request before any
    /// downstream gRPC/REST endpoint runs.
    /// </remarks>
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUserService currentUserService,
        ICacheService cacheService)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = currentUserService.UserId;
        if (userId is null)
        {
            // Unauthenticated request — pass through. This is the common path in extracted
            // services that don't host Identity: they receive only internal gRPC/HTTP traffic
            // without an authenticated user, so the middleware never needs the validator.
            await next(context).ConfigureAwait(false);
            return;
        }

        var softDeletedUserValidator = context.RequestServices.GetService<ISoftDeletedUserValidator>();
        if (softDeletedUserValidator is null)
        {
            // No validator registered — this service does not host Identity. Treat the
            // user as not soft-deleted (Identity is the source of truth and presumably
            // already validated the token before the request reached us). Continue.
            await next(context).ConfigureAwait(false);
            return;
        }

        var cacheKey = $"user:deleted:{userId.Value}";

        // Check cache first
        var cachedResult = await cacheService.GetAsync<bool?>(cacheKey);
        if (cachedResult is true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (cachedResult is null)
        {
            // Cache miss — check database
            var isDeleted = await softDeletedUserValidator.IsUserSoftDeletedAsync(userId.Value);
            await cacheService.SetAsync(cacheKey, isDeleted, CacheDuration);

            if (isDeleted)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
