using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Pages.Notifications;

/// <summary>
/// Form model for the push-notification compose page. Its DataAnnotations are the single declaration
/// of the field rules: the MudForm fields bridge to them through
/// <see cref="MMCA.Common.UI.Validation.ModelValidation.For"/> rather than repeating
/// <c>Required</c> / <c>RequiredError</c> in markup, and the length constants are the one place the
/// numbers live (the fields reference them for their input cap and character counter).
/// <para>
/// Each <c>ErrorMessage</c> is a resource key, resolved by the page's localizing
/// <see cref="MMCA.Common.UI.Validation.DataAnnotationsModelValidator"/> (ADR-027).
/// </para>
/// </summary>
public sealed class NotificationSendModel
{
    /// <summary>Maximum length of the notification title, mirroring the server-side invariant.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of the notification body, mirroring the server-side invariant.</summary>
    public const int BodyMaxLength = 2000;

    /// <summary>Notification title.</summary>
    [Required(ErrorMessage = "Notif.Send.Field.Title.Required")]
    [MaxLength(TitleMaxLength, ErrorMessage = "Notif.Send.Field.Title.MaxLength")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Notification body text.</summary>
    [Required(ErrorMessage = "Notif.Send.Field.Message.Required")]
    [MaxLength(BodyMaxLength, ErrorMessage = "Notif.Send.Field.Message.MaxLength")]
    public string Body { get; set; } = string.Empty;
}
