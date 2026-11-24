using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// The hub client's retry (ADR-044): up to three retries of a transient failure (server busy,
/// throttled, a timed-out connection or attempt) with exponential backoff (2, 4, 8 seconds plus up to
/// 8 percent jitter) capped at one minute, or the hub's own <c>Retry-After</c> wait when it sends
/// one. A permanent failure, and the caller's own cancellation, are never retried. These are the
/// defaults the Microsoft.Azure.NotificationHubs 4.2.0 SDK ran with, so a brownout is weathered as
/// before.
/// </summary>
internal sealed class NotificationHubRetryPolicy
{
    private const double JitterFactor = 0.08;

    internal NotificationHubRetryPolicy(int maxRetries, TimeSpan delay, TimeSpan maxDelay)
    {
        MaxRetries = maxRetries;
        Delay = delay;
        MaxDelay = maxDelay;
    }

    /// <summary>The production policy: three retries, a one-second base delay, a one-minute cap.</summary>
    internal static NotificationHubRetryPolicy Default { get; } = new(3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    internal int MaxRetries { get; }

    internal TimeSpan Delay { get; }

    internal TimeSpan MaxDelay { get; }

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying while <see cref="CalculateRetryDelay"/> allows. Each
    /// attempt builds a fresh request (new tracking id and token). A token cancelled before or between
    /// attempts ends the loop with <see cref="TaskCanceledException"/>.
    /// </summary>
    /// <typeparam name="T">The operation's result.</typeparam>
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var failedAttemptCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Every failure is classified by CalculateRetryDelay; a non-retryable one is rethrown unchanged below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ++failedAttemptCount;
                var retryDelay = CalculateRetryDelay(ex, failedAttemptCount);
                if (retryDelay.HasValue && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(retryDelay.Value, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
            }
        }

        throw new TaskCanceledException();
    }

    /// <summary>The wait before the next attempt, or null when the failure must not be retried.</summary>
    internal TimeSpan? CalculateRetryDelay(Exception lastException, int attemptCount)
    {
        if (MaxRetries <= 0
            || Delay == TimeSpan.Zero
            || MaxDelay == TimeSpan.Zero
            || attemptCount > MaxRetries
            || !ShouldRetry(lastException))
        {
            return null;
        }

#pragma warning disable CA5394 // Backoff jitter, not a security decision.
        var retryDelay = lastException is NotificationHubException { RetryAfter: { } retryAfter }
            ? retryAfter
            : TimeSpan.FromSeconds(Math.Pow(2, attemptCount) * Delay.TotalSeconds + Random.Shared.NextDouble() * Delay.TotalSeconds * JitterFactor);
#pragma warning restore CA5394

        return retryDelay > MaxDelay ? MaxDelay : retryDelay;
    }

    /// <summary>
    /// A transient hub failure, a timeout or a socket error. A cancellation counts only through its
    /// cause: an attempt that hit the client timeout (inner <see cref="TimeoutException"/>) retries,
    /// the caller's own cancellation (no inner exception) does not.
    /// </summary>
    private static bool ShouldRetry(Exception exception)
    {
        Exception? cause = exception switch
        {
            OperationCanceledException => exception.InnerException,
            AggregateException aggregate => aggregate.Flatten().InnerException,
            _ => exception,
        };

        return cause switch
        {
            NotificationHubException hubException => hubException.IsTransient,
            TimeoutException or SocketException => true,
            _ => false,
        };
    }
}
