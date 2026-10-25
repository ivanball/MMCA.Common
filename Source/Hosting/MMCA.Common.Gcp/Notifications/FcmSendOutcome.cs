using FirebaseAdmin.Messaging;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// The outcome of one message of a <see cref="IFcmMessagingClient.SendEachAsync"/> call: success, or
/// the FCM error code and message FirebaseAdmin reported for it.
/// </summary>
/// <param name="IsSuccess">Whether FCM accepted the message.</param>
/// <param name="ErrorCode">The FCM error code of a failure, when FCM gave one.</param>
/// <param name="Exception">The failure, or <see langword="null"/> on success.</param>
internal sealed record FcmSendOutcome(bool IsSuccess, MessagingErrorCode? ErrorCode, Exception? Exception)
{
    /// <summary>A successful send.</summary>
    internal static readonly FcmSendOutcome Success = new(true, null, null);

    /// <summary>
    /// Whether the failure says the registration token itself is dead, so its installation should be
    /// pruned: <see cref="MessagingErrorCode.Unregistered"/> (the app instance is gone), or an
    /// <see cref="MessagingErrorCode.InvalidArgument"/> whose message names the registration token
    /// (FCM answers a malformed token with "The registration token is not a valid FCM registration
    /// token"). Any other <c>InvalidArgument</c> is about the message, would fail every token alike,
    /// and must never prune them.
    /// </summary>
    internal bool IsStaleToken =>
        ErrorCode == MessagingErrorCode.Unregistered
        || ErrorCode == MessagingErrorCode.InvalidArgument
            && Exception?.Message.Contains("registration token", StringComparison.OrdinalIgnoreCase) == true;
}
