using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;

/// <summary>
/// Command to send a push notification to all recipients.
/// Embeds the <see cref="SendPushNotificationRequest"/> for automatic FluentValidation via
/// <see cref="ICommandWithRequest{TRequest}"/>.
/// </summary>
public sealed record SendPushNotificationCommand(
    SendPushNotificationRequest Request,
    UserIdentifierType SentByUserId) : ICommandWithRequest<SendPushNotificationRequest>
{
    /// <summary>
    /// Gets an optional deduplication key for the send (typically the caller's
    /// <c>Idempotency-Key</c> header). When present, a send whose key has already been seen
    /// returns the existing notification instead of creating a second one and sending again,
    /// so a retried request cannot deliver the same notification twice. When null (the default,
    /// which is what every existing caller gets) the send behaves exactly as before.
    /// </summary>
    public string? DedupKey { get; init; }
}
