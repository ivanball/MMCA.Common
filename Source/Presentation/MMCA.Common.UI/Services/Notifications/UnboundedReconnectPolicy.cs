using Microsoft.AspNetCore.SignalR.Client;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// The automatic reconnect schedule for <see cref="NotificationHubService"/>: it never gives up.
/// <para>
/// The SignalR default (0, 2, 10 and 30 seconds, then stop) abandons a connection after about 42
/// seconds, so a longer network drop left live notifications dead until a page reload. This policy
/// walks a short warm-up schedule and then retries every <see cref="MaxDelay"/> for as long as the
/// connection exists; the service ends the loop by disposing or stopping the connection, which is
/// the only way a reconnect stops.
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

        return retryContext.PreviousRetryCount < WarmUp.Length
            ? WarmUp[retryContext.PreviousRetryCount]
            : MaxDelay;
    }
}
