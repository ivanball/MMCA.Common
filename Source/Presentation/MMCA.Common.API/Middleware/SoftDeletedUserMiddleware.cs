using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;

namespace MMCA.Common.API.Middleware;

/// <summary>
/// Middleware that validates authenticated users have not been soft-deleted (BR-133).
/// If <c>User.IsDeleted = true</c> for the authenticated user's ID, the request is rejected with HTTP 401.
/// Uses a 30-second cache (<see cref="SoftDeletedUserCache"/>) to minimize per-request database lookups.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
/// <remarks>
/// <para>
/// This check fails OPEN: if the cache or the validator query throws, the failure is logged and
/// the request proceeds instead of being rejected. That is a deliberate security trade-off.
/// </para>
/// <para>
/// Failing closed would convert any cache or database blip into a complete outage for every
/// authenticated request across the application, since this middleware runs on the hot path of
/// all of them. The exposure it buys back is bounded: access tokens live 15 minutes, so a deleted
/// user's token is dead within that window regardless of what this middleware decides, and the
/// deletion itself already revoked the refresh token, so no new access token can be minted.
/// The residual risk is therefore a deleted user continuing to be served for at most the remaining
/// lifetime of one already-issued access token, and only while the cache or database is unhealthy.
/// </para>
/// </remarks>
public sealed partial class SoftDeletedUserMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Checks if the authenticated user has been soft-deleted and rejects the request if so.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="currentUserService">Service to access the current user's identity.</param>
    /// <param name="cacheService">Cache service for caching deleted user lookups.</param>
    /// <param name="logger">Logger used to report cache or validator failures that were failed open.</param>
    /// <returns>A task representing the middleware execution.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="ISoftDeletedUserValidator"/> is resolved lazily via
    /// <see cref="IServiceProvider.GetService(Type)"/> instead of being declared as an
    /// InvokeAsync parameter. The validator is implemented by the Identity module; in
    /// extracted services that do not host Identity (e.g., the Catalog microservice),
    /// no implementation is registered. Resolving lazily means the middleware no-ops
    /// in those services for unauthenticated requests, and only fails for authenticated
    /// requests with a clear error, instead of 500-ing every request before any
    /// downstream gRPC/REST endpoint runs.
    /// </para>
    /// <para>
    /// The logger is an InvokeAsync parameter (per-invoke injection) rather than a constructor
    /// parameter, matching how this convention-based middleware already takes its other services.
    /// </para>
    /// </remarks>
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUserService currentUserService,
        ICacheService cacheService,
        ILogger<SoftDeletedUserMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = currentUserService.UserId;
        if (userId is null)
        {
            // Unauthenticated request, pass through. This is the common path in extracted
            // services that don't host Identity: they receive only internal gRPC/HTTP traffic
            // without an authenticated user, so the middleware never needs the validator.
            await next(context).ConfigureAwait(false);
            return;
        }

        var softDeletedUserValidator = context.RequestServices.GetService<ISoftDeletedUserValidator>();
        if (softDeletedUserValidator is null)
        {
            // No validator registered: this service does not host Identity. Treat the
            // user as not soft-deleted (Identity is the source of truth and presumably
            // already validated the token before the request reached us). Continue.
            await next(context).ConfigureAwait(false);
            return;
        }

        var cacheKey = SoftDeletedUserCache.KeyFor(userId.Value);

        bool? cachedResult;
        var cacheReachable = true;
        try
        {
            cachedResult = await cacheService.GetAsync<bool?>(cacheKey, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open onto the validator query: a cache outage must not take down every
            // authenticated request. The write is skipped too, since the same store is down.
            LogCacheReadFailed(logger, cacheKey, ex);
            cachedResult = null;
            cacheReachable = false;
        }

        if (cachedResult is true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (cachedResult is null)
        {
            // Cache miss (or unreachable cache): check the database.
            bool isDeleted;
            try
            {
                isDeleted = await softDeletedUserValidator
                    .IsUserSoftDeletedAsync(userId.Value, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Both lookups are unavailable, so there is no evidence either way. Proceed
                // (see the fail-open rationale on the class), bounded by access-token lifetime.
                LogValidatorFailed(logger, userId.Value, ex);
                await next(context).ConfigureAwait(false);
                return;
            }

            if (cacheReachable)
            {
                try
                {
                    await cacheService
                        .SetAsync(cacheKey, isDeleted, SoftDeletedUserCache.MarkerDuration, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The authoritative answer is already in hand, so a failed cache write only
                    // costs the next request another database lookup. The request proceeds.
                    LogCacheWriteFailed(logger, cacheKey, ex);
                }
            }

            if (isDeleted)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Soft-deleted user cache read failed for key '{CacheKey}'; falling back to the validator query")]
    private static partial void LogCacheReadFailed(
        ILogger logger,
        string cacheKey,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Soft-deleted user cache write failed for key '{CacheKey}'; the request proceeds uncached")]
    private static partial void LogCacheWriteFailed(
        ILogger logger,
        string cacheKey,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Soft-deleted user validation failed for user '{UserId}'; the request is allowed through (fail open)")]
    private static partial void LogValidatorFailed(
        ILogger logger,
        UserIdentifierType userId,
        Exception exception);
}
