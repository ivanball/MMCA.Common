using System.Net;
using Microsoft.AspNetCore.SignalR.Client;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// The automatic reconnect schedule for <see cref="NotificationHubService"/>: it never gives up on a
/// transient failure.
/// <para>
/// The SignalR default (0, 2, 10 and 30 seconds, then stop) abandons a connection after about 42
/// seconds, so a longer network drop left live notifications dead until a page reload. This policy
/// walks a short warm-up schedule and then retries every <see cref="MaxDelay"/> for as long as the
/// connection exists; the service ends the loop by disposing or stopping the connection.
/// </para>
/// <para>
/// The one failure it does not retry is a refused authentication: a WebSocket handshake answered 401 or 403
/// (<see cref="IsAuthenticationRefused"/>) means the session expired or lost access, which another
/// attempt with the same credentials cannot fix, so the policy stops and the service does not restart.
/// </para>
/// </summary>
internal sealed class UnboundedReconnectPolicy : IRetryPolicy
{
    /// <summary>The longest wait between two reconnect attempts.</summary>
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] WarmUp =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];

    /// <inheritdoc />
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);

        if (IsAuthenticationRefused(retryContext.RetryReason))
        {
            return null;
        }

        return retryContext.PreviousRetryCount < WarmUp.Length
            ? WarmUp[retryContext.PreviousRetryCount]
            : MaxDelay;
    }

    /// <summary>
    /// Whether a connection failure is the server refusing authentication: an HTTP 401 or 403 answer
    /// (the WebSocket handshake, rethrown with its status by the connection's socket factory), found on the exception, its inner exceptions, or an aggregate's members.
    /// </summary>
    /// <param name="exception">The failure, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the server answered 401 or 403.</returns>
    internal static bool IsAuthenticationRefused(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Any(IsAuthenticationRefused);
            }
        }

        return false;
    }
}
