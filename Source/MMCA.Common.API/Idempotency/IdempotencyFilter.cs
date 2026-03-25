using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;

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
/// </remarks>
public sealed class IdempotencyFilter : IAsyncActionFilter
{
    /// <summary>
    /// Gets the name of the HTTP header that carries the client-provided idempotency key.
    /// </summary>
    public static string IdempotencyKeyHeader => "Idempotency-Key";

    private static string CacheKeyPrefix => "idempotency:";

    /// <summary>
    /// Cached responses expire after 24 hours, balancing deduplication safety against memory usage.
    /// </summary>
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromHours(24);

    /// <summary>
    /// Per-key semaphores prevent concurrent duplicate execution. Keys are cleaned up when
    /// no waiters remain, preventing unbounded memory growth.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyLocks = new();

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // No idempotency key header — execute normally without deduplication
        if (!context.HttpContext.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var keyValues)
            || string.IsNullOrWhiteSpace(keyValues.ToString()))
        {
            await next();
            return;
        }

        var idempotencyKey = keyValues.ToString();
        var cacheKey = $"{CacheKeyPrefix}{idempotencyKey}";
        var cache = context.HttpContext.RequestServices.GetRequiredService<ICacheService>();

        // Fast path: return cached response without acquiring a lock
        var cached = await cache.GetAsync<IdempotencyRecord>(cacheKey);
        if (cached is not null)
        {
            context.HttpContext.Response.Headers.Append("X-Idempotent-Replay", "true");
            context.Result = new ContentResult
            {
                StatusCode = cached.StatusCode,
                Content = cached.ResponseBody,
                ContentType = "application/json"
            };
            return;
        }

        // Slow path: acquire per-key lock to serialize concurrent duplicates
        var keyLock = KeyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(context.HttpContext.RequestAborted);
        try
        {
            // Double-check: another request may have completed and cached while we waited
            cached = await cache.GetAsync<IdempotencyRecord>(cacheKey);
            if (cached is not null)
            {
                context.HttpContext.Response.Headers.Append("X-Idempotent-Replay", "true");
                context.Result = new ContentResult
                {
                    StatusCode = cached.StatusCode,
                    Content = cached.ResponseBody,
                    ContentType = "application/json"
                };
                return;
            }

            var executedContext = await next();

            // Only cache ObjectResult responses (not redirects, file results, etc.)
            if (executedContext.Result is ObjectResult objectResult)
            {
                var record = new IdempotencyRecord(
                    objectResult.StatusCode ?? StatusCodes.Status200OK,
                    JsonSerializer.Serialize(objectResult.Value, JsonSerializerOptions.Web));

                await cache.SetAsync(cacheKey, record, DefaultExpiration);
            }
        }
        finally
        {
            keyLock.Release();

            // Eagerly remove the semaphore when no other requests are waiting on it
            if (keyLock.CurrentCount == 1)
                KeyLocks.TryRemove(cacheKey, out _);
        }
    }
}
