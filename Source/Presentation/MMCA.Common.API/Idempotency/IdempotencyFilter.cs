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
/// Uses a double-check locking pattern with per-key <see cref="SemaphoreSlim"/> instances to prevent
/// concurrent duplicate execution when multiple requests arrive with the same idempotency key
/// before the first completes. The flow is:
/// </para>
/// <list type="number">
///   <item>Check cache (fast path, no lock)</item>
///   <item>Acquire per-key semaphore</item>
///   <item>Re-check cache (another request may have completed while waiting)</item>
///   <item>Execute the action and cache the result</item>
/// </list>
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
    /// Serializes concurrent requests that map to the same cache key. Striped rather than
    /// one-semaphore-per-key: the key embeds a caller-supplied value, so a per-key table would
    /// either grow without bound or need an eager removal that races (a removal between another
    /// request's lookup and its wait lets a third request create a fresh semaphore, and both
    /// then execute concurrently, which is exactly what this lock exists to prevent).
    /// </summary>
    private static readonly KeyedSemaphoreStripe KeyLocks = new();

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // No idempotency key header — execute normally without deduplication
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

        // Slow path: acquire the key's stripe to serialize concurrent duplicates
        using (await KeyLocks.AcquireAsync(cacheKey, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            // Double-check: another request may have completed and cached while we waited
            if (await TryReplayAsync(context, cache, cacheKey).ConfigureAwait(false))
                return;

            var executedContext = await next().ConfigureAwait(false);
            await TryStoreAsync(context, cache, cacheKey, executedContext).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves the cached response for <paramref name="cacheKey"/> when one exists, returning
    /// whether the request was short-circuited.
    /// </summary>
    private static async Task<bool> TryReplayAsync(
        ActionExecutingContext context,
        ICacheService cache,
        string cacheKey)
    {
        var cached = await cache.GetAsync<IdempotencyRecord>(cacheKey).ConfigureAwait(false);
        if (cached is null)
            return false;

        context.HttpContext.Response.Headers.Append("X-Idempotent-Replay", "true");
        context.Result = new ContentResult
        {
            StatusCode = cached.StatusCode,
            Content = cached.ResponseBody,
            ContentType = "application/json"
        };

        return true;
    }

    /// <summary>
    /// Caches the executed response when it is a successful <see cref="ObjectResult"/>.
    /// Non-2xx results are deliberately not stored: replaying a failure for the whole retention
    /// window would mean a client retrying the same key after a transient 500 keeps receiving that
    /// 500 for 24 hours instead of the retry actually executing. Redirects and file results are
    /// skipped because the record carries only a status code and a JSON body.
    /// </summary>
    private static async Task TryStoreAsync(
        ActionExecutingContext context,
        ICacheService cache,
        string cacheKey,
        ActionExecutedContext executedContext)
    {
        if (executedContext.Result is not ObjectResult objectResult)
            return;

        var statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
        if (statusCode is < 200 or >= 300)
            return;

#pragma warning disable VSTHRD103 // JsonSerializer.Serialize to a string is correctly synchronous; SerializeAsync is only for writing to a stream.
        var record = new IdempotencyRecord(
            statusCode,
            JsonSerializer.Serialize(objectResult.Value, JsonSerializerOptions.Web));
#pragma warning restore VSTHRD103

        var idempotencySettings = context.HttpContext.RequestServices
            .GetService<IOptions<IdempotencySettings>>();
        var expiration = idempotencySettings is not null
            ? TimeSpan.FromHours(idempotencySettings.Value.CacheExpirationHours)
            : DefaultExpiration;

        await cache.SetAsync(cacheKey, record, expiration).ConfigureAwait(false);
    }

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
