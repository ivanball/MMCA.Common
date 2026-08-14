using System.Globalization;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Invariants;
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
    /// <summary>Maximum allowed length for the deduplication key.</summary>
    public const int DedupKeyMaxLength = 128;

    /// <summary>Maximum allowed length for the scope key.</summary>
    public const int ScopeKeyMaxLength = 128;

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

    /// <summary>
    /// Gets the optional deduplication key supplied by the caller (typically the
    /// <c>Idempotency-Key</c> header). A filtered unique index on this column lets the database
    /// arbitrate a race between two retried sends, so the same notification is never delivered
    /// twice. Null for every send that does not carry a key.
    /// </summary>
    public string? DedupKey { get; private set; }

    /// <summary>
    /// Gets the optional scope key stamped by the sending application (for example
    /// <c>"event:2"</c>). It is an opaque view filter, not a security boundary: a read that
    /// supplies a scope sees the notifications carrying that scope plus every unscoped one, while
    /// a read that supplies none still sees everything. Null for every send that carries no scope.
    /// </summary>
    public string? ScopeKey { get; private set; }

    /// <summary>EF Core parameterless constructor.</summary>
    private PushNotification()
    {
        Title = string.Empty;
        Body = string.Empty;
    }

    private PushNotification(
        string title,
        string body,
        UserIdentifierType sentByUserId,
        int recipientCount,
        string? dedupKey,
        string? scopeKey)
    {
        Title = title;
        Body = body;
        SentByUserId = sentByUserId;
        RecipientCount = recipientCount;
        Status = PushNotificationStatus.Pending;
        DedupKey = string.IsNullOrWhiteSpace(dedupKey) ? null : dedupKey;
        ScopeKey = string.IsNullOrWhiteSpace(scopeKey) ? null : scopeKey;
    }

    /// <summary>
    /// Factory method that creates a new <see cref="PushNotification"/> after validating invariants.
    /// Publishes a <see cref="PushNotificationCreated"/> domain event.
    /// </summary>
    /// <param name="title">The notification title (max 200 chars).</param>
    /// <param name="body">The notification body (max 2000 chars).</param>
    /// <param name="sentByUserId">The sender user identifier.</param>
    /// <param name="recipientCount">The number of targeted recipients.</param>
    /// <param name="dedupKey">
    /// Optional deduplication key (max 128 chars); null (the default) keeps the legacy behaviour
    /// where every send creates a new notification.
    /// </param>
    /// <param name="scopeKey">
    /// Optional scope key (max 128 chars); null (the default) keeps the legacy behaviour where the
    /// notification is visible to every read, scoped or not.
    /// </param>
    /// <returns>A <see cref="Result{T}"/> containing the created notification, or validation errors.</returns>
    public static Result<PushNotification> Create(
        string title,
        string body,
        UserIdentifierType sentByUserId,
        int recipientCount,
        string? dedupKey = null,
        string? scopeKey = null)
    {
        var result = Result.Combine(
            PushNotificationInvariants.EnsureTitleIsValid(title, nameof(Create)),
            PushNotificationInvariants.EnsureBodyIsValid(body, nameof(Create)),
            CommonInvariants.EnsureStringMaxLength(
                dedupKey,
                DedupKeyMaxLength,
                "PushNotification.DedupKey.TooLong",
                string.Create(CultureInfo.InvariantCulture, $"Notification dedup key cannot exceed {DedupKeyMaxLength} characters."),
                nameof(Create),
                nameof(dedupKey)),
            CommonInvariants.EnsureStringMaxLength(
                scopeKey,
                ScopeKeyMaxLength,
                "PushNotification.ScopeKey.TooLong",
                string.Create(CultureInfo.InvariantCulture, $"Notification scope key cannot exceed {ScopeKeyMaxLength} characters."),
                nameof(Create),
                nameof(scopeKey)));
        if (result.IsFailure)
        {
            return Result.Failure<PushNotification>(result.Errors);
        }

        var notification = new PushNotification(title, body, sentByUserId, recipientCount, dedupKey, scopeKey)
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
