using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Action filter that provides idempotency for write operations (POST, PUT, PATCH).
/// Clients include an <c>Idempotency-Key</c> header; the first response is cached
/// and replayed for subsequent requests with the same key within a 24-hour window.
/// </summary>
/// <remarks>
/// <para>
/// Uses a double-check locking pattern to prevent concurrent duplicate execution when multiple
/// requests arrive with the same idempotency key before the first completes. The flow is:
/// </para>
/// <list type="number">
///   <item>Check cache (fast path, no lock)</item>
///   <item>Acquire the key's lock</item>
///   <item>Re-check cache (another request may have completed while waiting)</item>
///   <item>Execute the action and cache the result</item>
/// </list>
/// <para>
/// The lock is an <see cref="IDistributedLock"/> when the host registers one, because a per-process
/// lock only serializes duplicates that land on the same replica, and both deployed apps run more
/// than one. Hosts without one fall back to the per-process
/// <see cref="KeyedSemaphoreStripe"/> below, which is correct for a single replica.
/// </para>
/// <para>
/// Replayed responses include the <c>X-Idempotent-Replay: true</c> header so clients can
/// distinguish cached responses from original executions.
/// </para>
/// <para>
/// SECURITY: the cache key is derived from the caller's identity, the HTTP method and the route
/// template in addition to the client-supplied key, so a key value is only ever replayed to the
/// caller that produced it, on the same endpoint. Keying on the bare client value would let two
/// callers who happen to choose the same key share an entry, replaying one user's serialized
/// response body to another.
/// </para>
/// </remarks>
public sealed class IdempotencyFilter : IAsyncActionFilter
{
    /// <summary>
    /// Gets the name of the HTTP header that carries the client-provided idempotency key.
    /// </summary>
    public static string IdempotencyKeyHeader => "Idempotency-Key";

    private static string CacheKeyPrefix => "idempotency:";

    /// <summary>Claim carrying the caller's identity, matching the one <c>TokenService</c> emits.</summary>
    private const string UserIdClaimType = "user_id";

    /// <summary>
    /// Default cache expiration when <see cref="IdempotencySettings"/> is not registered.
    /// </summary>
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromHours(24);

    /// <summary>
    /// Serializes concurrent requests that map to the same cache key when no
    /// <see cref="IDistributedLock"/> is registered. Striped rather than one-semaphore-per-key: the
    /// key embeds a caller-supplied value, so a per-key table would either grow without bound or
    /// need an eager removal that races (a removal between another request's lookup and its wait
    /// lets a third request create a fresh semaphore, and both then execute concurrently, which is
    /// exactly what this lock exists to prevent).
    /// </summary>
    private static readonly KeyedSemaphoreStripe KeyLocks = new();

    /// <summary>
    /// How long the distributed lock survives without release. Generous relative to a normal
    /// request so a slow action still finishes under its own lock, and short enough that a replica
    /// killed mid-action does not block the client's retry for long.
    /// </summary>
    private static readonly TimeSpan LockTimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for another replica to finish the same key before giving up. Sized to cover
    /// a typical action's duration: a duplicate that arrives while the original is still running
    /// usually waits this out and replays the stored response instead of being told to retry.
    /// </summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // No idempotency key header: execute normally without deduplication
        if (!context.HttpContext.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var keyValues)
            || string.IsNullOrWhiteSpace(keyValues.ToString()))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var cacheKey = BuildCacheKey(context, keyValues.ToString());
        var cache = context.HttpContext.RequestServices.GetRequiredService<ICacheService>();

        // Fast path: return cached response without acquiring a lock
        if (await TryReplayAsync(context, cache, cacheKey).ConfigureAwait(false))
            return;

        // Slow path. The lock has to span execute-and-store, so a duplicate cannot slip in between
        // the action finishing and its response reaching the cache.
        var distributedLock = context.HttpContext.RequestServices.GetService<IDistributedLock>();
        if (distributedLock is null)
        {
            await ExecuteUnderProcessLockAsync(context, next, cache, cacheKey).ConfigureAwait(false);
            return;
        }

        await ExecuteUnderDistributedLockAsync(context, next, cache, cacheKey, distributedLock).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the guarded section under the per-process stripe. Used when the host registers no
    /// <see cref="IDistributedLock"/>, which is the whole of a single-replica or test host.
    /// </summary>
    private static async Task ExecuteUnderProcessLockAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next,
        ICacheService cache,
        string cacheKey)
    {
        using (await KeyLocks.AcquireAsync(cacheKey, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            // Double-check: another request may have completed and cached while we waited
            if (await TryReplayAsync(context, cache, cacheKey).ConfigureAwait(false))
                return;

            await ExecuteAndStoreAsync(context, next, cache, cacheKey).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the guarded section under a lock every replica can see.
    /// </summary>
    /// <remarks>
    /// The per-process stripe this replaces only serializes duplicates that land on the SAME
    /// replica. Both deployed apps run at least two, so two duplicates that land on different
    /// replicas both missed the cache, both executed, and the second overwrote the first's stored
    /// response: the exact double execution the filter exists to prevent.
    /// <para>
    /// When the lock is held elsewhere, the holder is executing this same key right now. Waiting it
    /// out and replaying its stored response is the good outcome and the common one. If the wait
    /// expires with nothing stored, the action is still running (or its replica died), and the only
    /// two options left are to execute concurrently, which is the defect, or to tell the client the
    /// request is in flight. It gets 409 Conflict, which is retryable and honest, rather than a
    /// duplicate write.
    /// </para>
    /// </remarks>
    private static async Task ExecuteUnderDistributedLockAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next,
        ICacheService cache,
        string cacheKey,
        IDistributedLock distributedLock)
    {
        IAsyncDisposable? handle = await distributedLock
            .TryAcquireAsync(cacheKey, LockTimeToLive, LockWait, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (handle is null)
        {
            if (!await TryReplayAsync(context, cache, cacheKey).ConfigureAwait(false))
                context.Result = InFlightDuplicateResult();

            return;
        }

        await using (handle.ConfigureAwait(false))
        {
            // Double-check: the previous holder may have completed and cached while we waited
            if (await TryReplayAsync(context, cache, cacheKey).ConfigureAwait(false))
                return;

            await ExecuteAndStoreAsync(context, next, cache, cacheKey).ConfigureAwait(false);
        }
    }

    /// <summary>Executes the action and caches its response.</summary>
    private static async Task ExecuteAndStoreAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next,
        ICacheService cache,
        string cacheKey)
    {
        var executedContext = await next().ConfigureAwait(false);
        await TryStoreAsync(context, cache, cacheKey, executedContext).ConfigureAwait(false);
    }

    /// <summary>
    /// The response for a duplicate whose original is still executing on another replica. Deliberately
    /// not a replay: there is nothing stored yet, and executing would duplicate the write.
    /// </summary>
    private static ObjectResult InFlightDuplicateResult() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Request in progress",
            Detail = "A request with this Idempotency-Key is still being processed. Retry with the same key to receive its response.",
        })
        {
            StatusCode = StatusCodes.Status409Conflict,
        };

    /// <summary>
    /// Serves the cached response for <paramref name="cacheKey"/> when one exists, returning
    /// whether the request was short-circuited.
    /// </summary>
    /// <remarks>
    /// A stored record with an empty body came from a body-less result (204, or a bare status code),
    /// so it replays as a plain status code. Answering it as a <see cref="ContentResult"/> with
    /// <c>application/json</c> would put a content type on a response with no content, which the
    /// original did not have.
    /// </remarks>
    private static async Task<bool> TryReplayAsync(
        ActionExecutingContext context,
        ICacheService cache,
        string cacheKey)
    {
        var cached = await cache.GetAsync<IdempotencyRecord>(cacheKey).ConfigureAwait(false);
        if (cached is null)
            return false;

        context.HttpContext.Response.Headers.Append("X-Idempotent-Replay", "true");
        context.Result = string.IsNullOrEmpty(cached.ResponseBody)
            ? new StatusCodeResult(cached.StatusCode)
            : new ContentResult
            {
                StatusCode = cached.StatusCode,
                Content = cached.ResponseBody,
                ContentType = "application/json"
            };

        return true;
    }

    /// <summary>
    /// Caches the executed response when it is successful and representable as a status code plus an
    /// optional JSON body: an <see cref="ObjectResult"/> (which covers 200/201/202 with a payload)
    /// or a body-less <see cref="StatusCodeResult"/> such as the 204 from <c>NoContent()</c>.
    /// Non-2xx results are deliberately not stored: replaying a failure for the whole retention
    /// window would mean a client retrying the same key after a transient 500 keeps receiving that
    /// 500 for 24 hours instead of the retry actually executing. Redirects and file results are
    /// skipped because the record carries only a status code and a JSON body.
    /// </summary>
    /// <remarks>
    /// The 204 case was previously skipped, which left every command that answers <c>NoContent()</c>
    /// with no stored response at all: a duplicate re-executed the action instead of replaying, so
    /// the endpoints most likely to be retried (the body-less writes) were the ones idempotency did
    /// not actually cover.
    /// </remarks>
    private static async Task TryStoreAsync(
        ActionExecutingContext context,
        ICacheService cache,
        string cacheKey,
        ActionExecutedContext executedContext)
    {
        var record = BuildRecord(executedContext.Result);
        if (record is null)
            return;

        var idempotencySettings = context.HttpContext.RequestServices
            .GetService<IOptions<IdempotencySettings>>();
        var expiration = idempotencySettings is not null
            ? TimeSpan.FromHours(idempotencySettings.Value.CacheExpirationHours)
            : DefaultExpiration;

        await cache.SetAsync(cacheKey, record, expiration).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the cacheable snapshot of a result, or <see langword="null"/> when the result is not
    /// one this record shape can represent.
    /// </summary>
    private static IdempotencyRecord? BuildRecord(IActionResult? result)
    {
        switch (result)
        {
            case ObjectResult objectResult:
                var objectStatus = objectResult.StatusCode ?? StatusCodes.Status200OK;
#pragma warning disable VSTHRD103 // JsonSerializer.Serialize to a string is correctly synchronous; SerializeAsync is only for writing to a stream.
                return IsSuccess(objectStatus)
                    ? new IdempotencyRecord(
                        objectStatus,
                        JsonSerializer.Serialize(objectResult.Value, JsonSerializerOptions.Web))
                    : null;
#pragma warning restore VSTHRD103

            // NoContentResult and OkResult are StatusCodeResults, and so is anything from
            // StatusCode(int). The record's body is non-nullable, so a body-less response stores
            // the empty string and TryReplayAsync replays it without a content type.
            case StatusCodeResult statusCodeResult:
                return IsSuccess(statusCodeResult.StatusCode)
                    ? new IdempotencyRecord(statusCodeResult.StatusCode, string.Empty)
                    : null;

            default:
                return null;
        }
    }

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>
    /// Derives the cache key from the caller's identity, the HTTP method, the route template and
    /// the client-supplied key, hashed so the key length stays bounded regardless of what the
    /// client sends. Scoping to the caller stops one user's cached response from being replayed to
    /// another; scoping to method plus route stops the same key from colliding across endpoints
    /// (which, with services sharing one cache instance, would otherwise reach across services).
    /// </summary>
    private static string BuildCacheKey(ActionExecutingContext context, string idempotencyKey)
    {
        var subject = context.HttpContext.User?.FindFirst(UserIdClaimType)?.Value
            ?? string.Concat("anon:", context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        var route = context.ActionDescriptor.AttributeRouteInfo?.Template
            ?? context.HttpContext.Request.Path.Value
            ?? string.Empty;

        // \n is not valid in any component, so it cannot be used to forge a different tuple.
        var material = string.Join('\n', subject, context.HttpContext.Request.Method, route, idempotencyKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return string.Concat(CacheKeyPrefix, Convert.ToHexStringLower(hash));
    }
}
