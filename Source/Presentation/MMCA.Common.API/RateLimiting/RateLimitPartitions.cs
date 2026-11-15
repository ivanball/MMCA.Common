using System.Threading.RateLimiting;
using MMCA.Common.RateLimiting;

namespace MMCA.Common.API.RateLimiting;

/// <summary>
/// Partition factories for a host's own rate-limit policies, built on the same limiters
/// <c>AddCommonRateLimiting</c> uses, so a consumer policy answers a rejection the way the framework's
/// policies do.
/// </summary>
public static class RateLimitPartitions
{
    /// <summary>
    /// An in-memory fixed-window partition whose rejection carries the time left in the current
    /// window as its <see cref="MetadataName.RetryAfter"/> hint, rather than the whole window length
    /// that <see cref="RateLimitPartition.GetFixedWindowLimiter{TKey}"/> always reports. Use it in
    /// place of <c>GetFixedWindowLimiter</c> when the 429's <c>Retry-After</c> should tell the client
    /// how long it actually has to wait. No requests are queued: a request over the limit is rejected
    /// at once.
    /// </summary>
    /// <typeparam name="TKey">The partition key type.</typeparam>
    /// <param name="partitionKey">The partition key (for example the user id or client IP).</param>
    /// <param name="permitLimit">Requests allowed per window; must be positive.</param>
    /// <param name="window">The window length; must be positive.</param>
    /// <returns>The partition, for a <c>PartitionedRateLimiter.Create</c> or <c>AddPolicy</c> selector.</returns>
    public static RateLimitPartition<TKey> FixedWindowWithRemainingRetryAfter<TKey>(TKey partitionKey, int permitLimit, TimeSpan window) =>
        FixedWindowWithRemainingRetryAfter(partitionKey, permitLimit, window, timeProvider: null);

    /// <summary>
    /// <see cref="FixedWindowWithRemainingRetryAfter{TKey}(TKey, int, TimeSpan)"/> with a substitutable
    /// clock for the window start, so tests can drive the hint.
    /// </summary>
    /// <typeparam name="TKey">The partition key type.</typeparam>
    /// <param name="partitionKey">The partition key.</param>
    /// <param name="permitLimit">Requests allowed per window; must be positive.</param>
    /// <param name="window">The window length; must be positive.</param>
    /// <param name="timeProvider">The clock for the window start; <see langword="null"/> for the system clock.</param>
    /// <returns>The partition.</returns>
    internal static RateLimitPartition<TKey> FixedWindowWithRemainingRetryAfter<TKey>(
        TKey partitionKey,
        int permitLimit,
        TimeSpan window,
        TimeProvider? timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        return RateLimitPartition.Get(partitionKey, _ => new RemainingWindowFixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions
            {
                Window = window,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            },
            timeProvider));
    }
}
