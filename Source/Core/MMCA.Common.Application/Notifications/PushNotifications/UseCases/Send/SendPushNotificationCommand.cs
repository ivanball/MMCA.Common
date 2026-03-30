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
    UserIdentifierType SentByUserId) : ICommandWithRequest<SendPushNotificationRequest>;
