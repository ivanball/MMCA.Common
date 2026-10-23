using System.Globalization;
using System.Net;

namespace MMCA.Common.Shared.Resilience;

/// <summary>
/// Both ends of the <c>Retry-After</c> contract on a <c>429 Too Many Requests</c>. The server half
/// turns a rejected rate-limit lease into the header value the rate-limiter <c>OnRejected</c>
/// callbacks write; the client half reads the header back and decides whether a retry is worth
/// waiting for under <see cref="HttpResilienceDefaults.MaxRetryAfterWait"/>. Kept in Shared, on pure
/// BCL types, so the API, Aspire and UI.Web limiters and the UI and Aspire retry policies all apply
/// one rule. <c>MMCA.Common.Gateway</c> references no MMCA package and carries an internal copy of
/// the server half, pinned to this one by a test.
/// </summary>
public static class RetryAfterHeader
{
    /// <summary>
    /// Formats the <c>Retry-After</c> value for a rejected rate-limit lease's
    /// <c>MetadataName.RetryAfter</c> hint: whole seconds, rounded up so a client never retries before
    /// the window has actually moved on, and never negative. Takes the hint rather than the lease
    /// because <c>System.Threading.RateLimiting</c> ships in the ASP.NET Core shared framework, which
    /// this package (also loaded by Blazor WebAssembly) does not reference; each <c>OnRejected</c>
    /// callback reads the metadata and a lease without it (a concurrency-limiter rejection, for one)
    /// sends the 429 without the header.
    /// </summary>
    /// <param name="retryAfter">The lease's retry-after hint.</param>
    /// <returns>The header value, in whole seconds.</returns>
    public static string Format(TimeSpan retryAfter)
    {
        var seconds = Math.Max(0d, Math.Ceiling(retryAfter.TotalSeconds));
        return ((long)seconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the delay a response's <c>Retry-After</c> header asks for, in either form: a delta in
    /// seconds, or an HTTP date measured against <paramref name="timeProvider"/> (a date already past
    /// is a zero delay).
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <param name="timeProvider">The clock a date-form header is measured against.</param>
    /// <param name="delay">The requested delay, when the header is present.</param>
    /// <returns><see langword="true"/> when the response carries a usable <c>Retry-After</c>.</returns>
    public static bool TryGetDelay(HttpResponseMessage response, TimeProvider timeProvider, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            delay = delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            return true;
        }

        if (header?.Date is { } date)
        {
            var remaining = date - timeProvider.GetUtcNow();
            delay = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            return true;
        }

        delay = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// Decides whether a response is a <c>429</c> whose <c>Retry-After</c> asks for longer than
    /// <see cref="HttpResilienceDefaults.MaxRetryAfterWait"/>. Such a response is not worth retrying:
    /// the caller sees the refusal at once instead of a connection held open for the rest of the
    /// window. A 429 without the header, or with one at or under the budget, is not over budget.
    /// </summary>
    /// <param name="response">The response to classify; <see langword="null"/> (an exception outcome) is never over budget.</param>
    /// <param name="timeProvider">The clock a date-form header is measured against.</param>
    /// <returns><see langword="true"/> when the 429 asks for a wait longer than the budget.</returns>
    public static bool ExceedsRetryBudget(HttpResponseMessage? response, TimeProvider timeProvider) =>
        response is { StatusCode: HttpStatusCode.TooManyRequests }
        && TryGetDelay(response, timeProvider, out var delay)
        && delay > HttpResilienceDefaults.MaxRetryAfterWait;
}
