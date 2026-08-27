using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Concurrency;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Filter that provides idempotency for write operations (POST, PUT, PATCH).
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
/// The filter runs at BOTH the resource stage and the action stage. The resource stage runs before
/// model binding, which is the only point at which the request body can still be made re-readable,
/// so that is where <c>EnableBuffering</c> is
/// called (only when the header is present, so ordinary traffic pays nothing). The action stage
/// then hashes the buffered body and binds it to the cached record: replaying a stored response to
/// a request that carries a DIFFERENT payload would silently swallow a genuinely new write, so a
/// key reused with a different body is answered 422 rather than replayed.
/// </para>
/// <para>
/// The cache and the lock are treated as best-effort infrastructure. A cache read, a cache write or
/// a lock acquisition that faults is logged, counted on <c>idempotency.degraded</c>, and swallowed:
/// the request executes without the idempotency guarantee instead of failing. Deduplication is an
/// optimization over an at-least-once client retry, so a cache outage must not become an outage of
/// every write endpoint that carries the attribute.
/// </para>
/// <para>
/// SECURITY: the cache key is derived from the caller's identity, the HTTP method and the route
/// template in addition to the client-supplied key, so a key value is only ever replayed to the
/// caller that produced it, on the same endpoint. Keying on the bare client value would let two
/// callers who happen to choose the same key share an entry, replaying one user's serialized
/// response body to another.
/// </para>
/// </remarks>
public sealed partial class IdempotencyFilter(ILogger<IdempotencyFilter> logger)
    : IAsyncActionFilter, IAsyncResourceFilter
{
    /// <summary>
    /// Gets the name of the HTTP header that carries the client-provided idempotency key.
    /// </summary>
    public static string IdempotencyKeyHeader => IdempotencyHeaders.IdempotencyKey;

    private static string CacheKeyPrefix => "idempotency:";

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

    /// <summary>
    /// Hash of a zero-length payload, used for body-less requests and for the request whose body
    /// stream cannot be rewound (nothing to compare, so every such request agrees with itself).
    /// </summary>
    private static readonly string EmptyBodyHash =
        Convert.ToHexStringLower(SHA256.HashData([]));

    /// <inheritdoc />
    /// <remarks>
    /// Runs before model binding, which is the last point at which the body can be made replayable.
    /// Buffering is enabled only for requests that actually carry an idempotency key: turning it on
    /// unconditionally would spool every request body of every action the filter is attached to.
    /// </remarks>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (ReadIdempotencyKey(context.HttpContext.Request) is not null)
            context.HttpContext.Request.EnableBuffering();

        await next().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // No idempotency key header: execute normally without deduplication
        var idempotencyKey = ReadIdempotencyKey(context.HttpContext.Request);
        if (idempotencyKey is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var cacheKey = BuildCacheKey(context, idempotencyKey);
        var requestBodyHash = await ComputeRequestBodyHashAsync(context.HttpContext).ConfigureAwait(false);
        var cache = context.HttpContext.RequestServices.GetRequiredService<ICacheService>();

        // Fast path: return cached response without acquiring a lock
        if (await TryReplayAsync(context, cache, cacheKey, requestBodyHash).ConfigureAwait(false))
            return;

        // Slow path. The lock has to span execute-and-store, so a duplicate cannot slip in between
        // the action finishing and its response reaching the cache.
        var distributedLock = context.HttpContext.RequestServices.GetService<IDistributedLock>();
        if (distributedLock is null)
        {
            await ExecuteUnderProcessLockAsync(context, next, cache, cacheKey, requestBodyHash).ConfigureAwait(false);
            return;
        }

        await ExecuteUnderDistributedLockAsync(context, next, cache, cacheKey, requestBodyHash, distributedLock)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the client-supplied idempotency key, or <see langword="null"/> when the header is
    /// absent or blank. Both filter stages go through this so they agree on what "has a key" means.
    /// </summary>
    private static string? ReadIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(IdempotencyHeaders.IdempotencyKey, out var keyValues))
            return null;

        var key = keyValues.ToString();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>
    /// Hashes the request body so a replay can be refused when the same key arrives with a
    /// different payload.
    /// </summary>
    /// <remarks>
    /// The stream is rewound before reading AND after hashing, because model binding (or any later
    /// reader) still has to see the whole body. A stream that cannot seek was never buffered, so
    /// there is nothing to hash and nothing to compare against: it takes the empty-body hash rather
    /// than throwing, which leaves such a request exactly as idempotent as it was before.
    /// </remarks>
    private static async Task<string> ComputeRequestBodyHashAsync(HttpContext httpContext)
    {
        var body = httpContext.Request.Body;
        if (!body.CanSeek)
            return EmptyBodyHash;

        body.Position = 0;
        var hash = await SHA256.HashDataAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        body.Position = 0;

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Runs the guarded section under the per-process stripe. Used when the host registers no
    /// <see cref="IDistributedLock"/>, which is the whole of a single-replica or test host.
    /// </summary>
    private async Task ExecuteUnderProcessLockAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next,
        ICacheService cache,
        string cacheKey,
        string requestBodyHash)
    {
        using (await KeyLocks.AcquireAsync(cacheKey, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            // Double-check: another request may have completed and cached while we waited
            if (await TryReplayAsync(context, cache, cacheKey, requestBodyHash).ConfigureAwait(false))
                return;

            await ExecuteAndStoreAsync(context, next, cache, cacheKey, requestBodyHash).ConfigureAwait(false);
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
    /// <para>
    /// A lock backend that FAULTS is a different case from a lock that is held: there is no holder
    /// to wait for and no answer to replay, so refusing the request would turn a Redis blip into a
    /// write outage. The action runs unguarded and the degradation is counted instead.
    /// </para>
    /// </remarks>
    private async Task ExecuteUnderDistributedLockAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next,
        ICacheService cache,
        string cacheKey,
        string requestBodyHash,
        IDistributedLock distributedLock)
    {
        IAsyncDisposable? handle;
        try
        {
            handle = await distributedLock
                .TryAcquireAsync(cacheKey, LockTimeToLive, LockWait, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IdempotencyMetrics.RecordDegraded();
            LogLockUnavailable(cacheKey, ex);
            await ExecuteAndStoreAsync(context, next, cache, cacheKey, requestBodyHash).ConfigureAwait(false);
            return;
        }

        if (handle is null)
        {
            LogLockWaitTimedOut(cacheKey);

            if (!await TryReplayAsync(context, cache, cacheKey, requestBodyHash).ConfigureAwait(false))
            {
                IdempotencyMetrics.RecordConflict(IdempotencyMetrics.ConflictKindInFlight);
                LogDuplicateInFlight(cacheKey);
                context.Result = InFlightDuplicateResult();
            }

            return;
        }

        await using (handle.ConfigureAwait(false))
        {
            // Double-check: the previous holder may have completed and cached while we waited
            if (await TryReplayAsync(context, cache, cacheKey, requestBodyHash).ConfigureAwait(false))
                return;

            await ExecuteAndStoreAsync(context, next, cache, cacheKey, requestBodyHash).ConfigureAwait(false);
        }
    }

    /// <summary>Executes the action and caches its response.</summary>
    private async Task ExecuteAndStoreAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next,
        ICacheService cache,
        string cacheKey,
        string requestBodyHash)
    {
        var executedContext = await next().ConfigureAwait(false);
        await TryStoreAsync(context, cache, cacheKey, requestBodyHash, executedContext).ConfigureAwait(false);
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
    /// The response for a key that already answered a DIFFERENT payload. Replaying the stored
    /// response here would tell the client its new write succeeded when nothing ran, so the reuse
    /// is reported instead.
    /// </summary>
    /// <remarks>
    /// 422 rather than 409: this filter already spends 409 on "the original is still in flight",
    /// which is a retry-with-the-same-key situation. Key reuse with a different body is not
    /// retryable at all until the client picks a new key, so the two need distinct status codes.
    /// </remarks>
    private static ObjectResult BodyMismatchResult() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Idempotency-Key reuse",
            Detail = "The Idempotency-Key was already used with a different request body.",
        })
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
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
    /// <para>
    /// A record whose <see cref="IdempotencyRecord.RequestBodyHash"/> is <see langword="null"/> was
    /// written before the body was bound to the key, so there is nothing to compare and it replays
    /// unconditionally. Those entries expire with the retention window.
    /// </para>
    /// <para>
    /// A cache read that faults is reported as "nothing stored" so the request executes: an
    /// unavailable cache must degrade deduplication, not the endpoint.
    /// </para>
    /// </remarks>
    private async Task<bool> TryReplayAsync(
        ActionExecutingContext context,
        ICacheService cache,
        string cacheKey,
        string requestBodyHash)
    {
        IdempotencyRecord? cached;
        try
        {
            cached = await cache.GetAsync<IdempotencyRecord>(cacheKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IdempotencyMetrics.RecordDegraded();
            LogCacheReadFailed(cacheKey, ex);
            return false;
        }

        if (cached is null)
            return false;

        if (cached.RequestBodyHash is not null
            && !string.Equals(cached.RequestBodyHash, requestBodyHash, StringComparison.Ordinal))
        {
            IdempotencyMetrics.RecordConflict(IdempotencyMetrics.ConflictKindBodyMismatch);
            LogRequestBodyMismatch(cacheKey);
            context.Result = BodyMismatchResult();
            return true;
        }

        IdempotencyMetrics.RecordReplayed();
        LogReplayServed(cacheKey, cached.StatusCode);

        context.HttpContext.Response.Headers.Append(IdempotencyHeaders.IdempotentReplay, "true");
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
    /// <para>
    /// A store that faults is swallowed. The action already ran and its response is already the
    /// client's answer; failing here would turn a successful write into an error the client would
    /// retry, which is the duplicate this filter exists to prevent.
    /// </para>
    /// </remarks>
    private async Task TryStoreAsync(
        ActionExecutingContext context,
        ICacheService cache,
        string cacheKey,
        string requestBodyHash,
        ActionExecutedContext executedContext)
    {
        var record = BuildRecord(executedContext.Result, requestBodyHash);
        if (record is null)
            return;

        var idempotencySettings = context.HttpContext.RequestServices
            .GetService<IOptions<IdempotencySettings>>();
        var expiration = idempotencySettings is not null
            ? TimeSpan.FromHours(idempotencySettings.Value.CacheExpirationHours)
            : DefaultExpiration;

        try
        {
            await cache.SetAsync(cacheKey, record, expiration).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IdempotencyMetrics.RecordDegraded();
            LogCacheStoreFailed(cacheKey, ex);
        }
    }

    /// <summary>
    /// Builds the cacheable snapshot of a result, or <see langword="null"/> when the result is not
    /// one this record shape can represent.
    /// </summary>
    private static IdempotencyRecord? BuildRecord(IActionResult? result, string requestBodyHash)
    {
        switch (result)
        {
            case ObjectResult objectResult:
                var objectStatus = objectResult.StatusCode ?? StatusCodes.Status200OK;
#pragma warning disable VSTHRD103 // JsonSerializer.Serialize to a string is correctly synchronous; SerializeAsync is only for writing to a stream.
                return IsSuccess(objectStatus)
                    ? new IdempotencyRecord(
                        objectStatus,
                        JsonSerializer.Serialize(objectResult.Value, JsonSerializerOptions.Web),
                        requestBodyHash)
                    : null;
#pragma warning restore VSTHRD103

            // NoContentResult and OkResult are StatusCodeResults, and so is anything from
            // StatusCode(int). The record's body is non-nullable, so a body-less response stores
            // the empty string and TryReplayAsync replays it without a content type.
            case StatusCodeResult statusCodeResult:
                return IsSuccess(statusCodeResult.StatusCode)
                    ? new IdempotencyRecord(statusCodeResult.StatusCode, string.Empty, requestBodyHash)
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
        var subject = context.HttpContext.User.FindUserIdValue()
            ?? string.Concat("anon:", context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        var route = context.ActionDescriptor.AttributeRouteInfo?.Template
            ?? context.HttpContext.Request.Path.Value
            ?? string.Empty;

        // \n is not valid in any component, so it cannot be used to forge a different tuple.
        var material = string.Join('\n', subject, context.HttpContext.Request.Method, route, idempotencyKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return string.Concat(CacheKeyPrefix, Convert.ToHexStringLower(hash));
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Idempotency replay served for key {CacheKey} with status {StatusCode}.")]
    private static partial void LogReplayServed(ILogger logger, string cacheKey, int statusCode);

    private void LogReplayServed(string cacheKey, int statusCode) =>
        LogReplayServed(logger, cacheKey, statusCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Idempotency key {CacheKey} was reused with a different request body; the request was rejected instead of replayed.")]
    private static partial void LogRequestBodyMismatch(ILogger logger, string cacheKey);

    private void LogRequestBodyMismatch(string cacheKey) => LogRequestBodyMismatch(logger, cacheKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Idempotency key {CacheKey} is still being processed elsewhere and nothing is stored yet; the duplicate was told to retry.")]
    private static partial void LogDuplicateInFlight(ILogger logger, string cacheKey);

    private void LogDuplicateInFlight(string cacheKey) => LogDuplicateInFlight(logger, cacheKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Timed out waiting for the idempotency lock on key {CacheKey}.")]
    private static partial void LogLockWaitTimedOut(ILogger logger, string cacheKey);

    private void LogLockWaitTimedOut(string cacheKey) => LogLockWaitTimedOut(logger, cacheKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Idempotency cache read failed for key {CacheKey}; the request will execute without deduplication.")]
    private static partial void LogCacheReadFailed(ILogger logger, string cacheKey, Exception exception);

    private void LogCacheReadFailed(string cacheKey, Exception exception) =>
        LogCacheReadFailed(logger, cacheKey, exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Idempotency cache store failed for key {CacheKey}; the response was returned but a duplicate will re-execute.")]
    private static partial void LogCacheStoreFailed(ILogger logger, string cacheKey, Exception exception);

    private void LogCacheStoreFailed(string cacheKey, Exception exception) =>
        LogCacheStoreFailed(logger, cacheKey, exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Idempotency lock could not be acquired for key {CacheKey}; the action ran without the idempotency guarantee.")]
    private static partial void LogLockUnavailable(ILogger logger, string cacheKey, Exception exception);

    private void LogLockUnavailable(string cacheKey, Exception exception) =>
        LogLockUnavailable(logger, cacheKey, exception);
}
