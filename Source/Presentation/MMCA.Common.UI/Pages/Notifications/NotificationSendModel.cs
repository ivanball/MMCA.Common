using System.ComponentModel.DataAnnotations;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Form model for the push-notification compose page. Its DataAnnotations are the single declaration
/// of the field rules: the MudForm fields bridge to them through
/// <see cref="MMCA.Common.UI.Validation.ModelValidation.For"/> rather than repeating
/// <c>Required</c> / <c>RequiredError</c> in markup. The length numbers are NOT declared here: they
/// come from <see cref="SendPushNotificationRequest"/>, the contract the endpoint binds, which is
/// also where the compose form reads its input cap and character counter from. One number, shared
/// by the client cap, the client message and the server invariant.
/// <para>
/// Each <c>ErrorMessage</c> is a resource key, resolved by the page's localizing
/// <see cref="MMCA.Common.UI.Validation.DataAnnotationsModelValidator"/> (ADR-027).
/// </para>
/// </summary>
public sealed class NotificationSendModel
{
    /// <summary>Notification title.</summary>
    [Required(ErrorMessage = "Notif.Send.Field.Title.Required")]
    [MaxLength(SendPushNotificationRequest.TitleMaxLength, ErrorMessage = "Notif.Send.Field.Title.MaxLength")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Notification body text.</summary>
    [Required(ErrorMessage = "Notif.Send.Field.Message.Required")]
    [MaxLength(SendPushNotificationRequest.BodyMaxLength, ErrorMessage = "Notif.Send.Field.Message.MaxLength")]
    public string Body { get; set; } = string.Empty;
}
