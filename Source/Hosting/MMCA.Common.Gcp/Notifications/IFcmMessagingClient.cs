using FirebaseAdmin.Messaging;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// The slice of <see cref="FirebaseMessaging"/> FCM push uses. FCM has no emulator and FirebaseAdmin's
/// response and exception types cannot be constructed outside the SDK, so the sender talks to this
/// interface and their rules are unit-tested against a fake;
/// <see cref="FirebaseMessagingClient"/> is the implementation over a named Firebase app.
/// </summary>
internal interface IFcmMessagingClient
{
    /// <summary>
    /// The most messages one <see cref="SendEachAsync"/> call takes (FirebaseAdmin's own limit on
    /// <c>SendEachAsync</c>).
    /// </summary>
    internal const int MaxMessagesPerSend = 500;

    /// <summary>Sends each message as its own FCM HTTP v1 request.</summary>
    /// <param name="messages">At most <see cref="MaxMessagesPerSend"/> messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One outcome per message, in order.</returns>
    Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}
