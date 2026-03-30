using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Shared.Notifications.PushNotifications;

/// <summary>
/// Data transfer object for <c>PushNotification</c> entity.
/// </summary>
public record class PushNotificationDTO : IBaseDTO<PushNotificationIdentifierType>
{
    /// <summary>Gets or inits the notification identifier.</summary>
    public required PushNotificationIdentifierType Id { get; init; }

    /// <summary>Gets or inits the notification title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets or inits the notification body.</summary>
    public required string Body { get; init; }

    /// <summary>Gets or inits the user who sent the notification.</summary>
    public required UserIdentifierType SentByUserId { get; init; }

    /// <summary>Gets or inits the number of recipients at time of send.</summary>
    public required int RecipientCount { get; init; }

    /// <summary>Gets or inits the delivery status.</summary>
    public required string Status { get; init; }

    /// <summary>Gets or inits the creation timestamp.</summary>
    public DateTime CreatedOn { get; init; }
}
