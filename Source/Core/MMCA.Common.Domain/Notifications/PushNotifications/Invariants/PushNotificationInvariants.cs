using MMCA.Common.Domain.Invariants;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Notifications.PushNotifications.Invariants;

/// <summary>
/// Domain invariant rules for <see cref="PushNotification"/>.
/// </summary>
public static class PushNotificationInvariants
{
    /// <summary>Maximum allowed length for notification title.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum allowed length for notification body.</summary>
    public const int BodyMaxLength = 2000;

    public static Result EnsureTitleIsValid(string title, string source) =>
        Result.Combine(
            CommonInvariants.EnsureStringIsNotEmpty(title, "PushNotification.Title.Empty", "Notification title cannot be empty.", source, nameof(title)),
            CommonInvariants.EnsureStringMaxLength(title, TitleMaxLength, "PushNotification.Title.TooLong", $"Notification title cannot exceed {TitleMaxLength} characters.", source, nameof(title)));

    public static Result EnsureBodyIsValid(string body, string source) =>
        Result.Combine(
            CommonInvariants.EnsureStringIsNotEmpty(body, "PushNotification.Body.Empty", "Notification body cannot be empty.", source, nameof(body)),
            CommonInvariants.EnsureStringMaxLength(body, BodyMaxLength, "PushNotification.Body.TooLong", $"Notification body cannot exceed {BodyMaxLength} characters.", source, nameof(body)));
}
