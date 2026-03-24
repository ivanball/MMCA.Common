using Microsoft.AspNetCore.Http;
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
    /// <param name="softDeletedUserValidator">Validator to check if a user is soft-deleted.</param>
    /// <returns>A task representing the middleware execution.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUserService currentUserService,
        ICacheService cacheService,
        ISoftDeletedUserValidator softDeletedUserValidator)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = currentUserService.UserId;
        if (userId is null)
        {
            // Unauthenticated request — pass through
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
