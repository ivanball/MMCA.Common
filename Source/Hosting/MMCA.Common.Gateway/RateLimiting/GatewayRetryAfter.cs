using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace MMCA.Common.Gateway.RateLimiting;

/// <summary>
/// The <c>Retry-After</c> header writer for the route-policy limiter. An internal copy of
/// <c>MMCA.Common.Shared.Resilience.RetryAfterHeader.Format</c> plus the <c>OnRejected</c> wrapper the
/// other MMCA limiters use, because this package deliberately references no MMCA package (see the
/// project file). A test pins this copy to the Shared one, so the two cannot drift.
/// </summary>
internal static class GatewayRetryAfter
{
    /// <summary>
    /// Formats a lease's retry-after hint as a <c>Retry-After</c> value: whole seconds, rounded up
    /// so a client never retries before the window has moved on, and never negative.
    /// </summary>
    /// <param name="retryAfter">The lease's retry-after hint.</param>
    /// <returns>The header value, in whole seconds.</returns>
    internal static string Format(TimeSpan retryAfter)
    {
        var seconds = Math.Max(0d, Math.Ceiling(retryAfter.TotalSeconds));
        return ((long)seconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Wraps a rate limiter's <c>OnRejected</c> callback so every 429 carries a <c>Retry-After</c>
    /// header, in whole seconds rounded up, whenever the rejecting lease has a
    /// <see cref="MetadataName.RetryAfter"/> hint (the fixed-window limiter does; a concurrency
    /// rejection does not, and sends no header). A callback configured before this registration
    /// still runs, after the header is written.
    /// </summary>
    /// <param name="previous">The callback already configured, if any.</param>
    /// <returns>The callback to assign to <c>RateLimiterOptions.OnRejected</c>.</returns>
    internal static Func<OnRejectedContext, CancellationToken, ValueTask> WithRetryAfterHeader(
        Func<OnRejectedContext, CancellationToken, ValueTask>? previous) =>
        (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = Format(retryAfter);
            }

            return previous?.Invoke(context, cancellationToken) ?? ValueTask.CompletedTask;
        };
}
