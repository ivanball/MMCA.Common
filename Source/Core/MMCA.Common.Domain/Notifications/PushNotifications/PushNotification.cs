using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Notifications.PushNotifications.DomainEvents;
using MMCA.Common.Domain.Notifications.PushNotifications.Invariants;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Notifications.PushNotifications;

/// <summary>
/// Aggregate root representing a push notification sent to recipients.
/// Tracks delivery status for audit purposes.
/// </summary>
[IdValueGenerated]
public sealed class PushNotification : AuditableAggregateRootEntity<PushNotificationIdentifierType>
{
    /// <summary>Gets the notification title.</summary>
    public string Title { get; private set; }

    /// <summary>Gets the notification body text.</summary>
    public string Body { get; private set; }

    /// <summary>Gets the user identifier of the sender.</summary>
    public UserIdentifierType SentByUserId { get; private set; }

    /// <summary>Gets the number of recipients at time of send.</summary>
    public int RecipientCount { get; private set; }

    /// <summary>Gets the delivery status of the notification.</summary>
    public PushNotificationStatus Status { get; private set; }

    /// <summary>EF Core parameterless constructor.</summary>
    private PushNotification()
    {
        Title = string.Empty;
        Body = string.Empty;
    }

    private PushNotification(string title, string body, UserIdentifierType sentByUserId, int recipientCount)
    {
        Title = title;
        Body = body;
        SentByUserId = sentByUserId;
        RecipientCount = recipientCount;
        Status = PushNotificationStatus.Pending;
    }

    /// <summary>
    /// Factory method that creates a new <see cref="PushNotification"/> after validating invariants.
    /// Publishes a <see cref="PushNotificationCreated"/> domain event.
    /// </summary>
    /// <param name="title">The notification title (max 200 chars).</param>
    /// <param name="body">The notification body (max 2000 chars).</param>
    /// <param name="sentByUserId">The sender user identifier.</param>
    /// <param name="recipientCount">The number of targeted recipients.</param>
    /// <returns>A <see cref="Result{T}"/> containing the created notification, or validation errors.</returns>
    public static Result<PushNotification> Create(
        string title,
        string body,
        UserIdentifierType sentByUserId,
        int recipientCount)
    {
        var result = Result.Combine(
            PushNotificationInvariants.EnsureTitleIsValid(title, nameof(Create)),
            PushNotificationInvariants.EnsureBodyIsValid(body, nameof(Create)));
        if (result.IsFailure)
        {
            return Result.Failure<PushNotification>(result.Errors);
        }

        var notification = new PushNotification(title, body, sentByUserId, recipientCount)
        {
            Id = default
        };

        notification.AddDomainEvent(new PushNotificationCreated(default, title, recipientCount));

        return Result.Success(notification);
    }

    /// <summary>
    /// Marks the notification as successfully sent.
    /// </summary>
    public void MarkAsSent() => Status = PushNotificationStatus.Sent;

    /// <summary>
    /// Marks the notification as failed.
    /// </summary>
    public void MarkAsFailed() => Status = PushNotificationStatus.Failed;
}
