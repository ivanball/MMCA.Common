using System.Globalization;
using FluentValidation;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.PushNotifications.Invariants;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;

/// <summary>
/// FluentValidation validator for <see cref="SendPushNotificationRequest"/>.
/// </summary>
public sealed class SendPushNotificationRequestValidator : AbstractValidator<SendPushNotificationRequest>
{
    public SendPushNotificationRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Notification title is required.")
            .MaximumLength(PushNotificationInvariants.TitleMaxLength)
            .WithMessage(string.Create(CultureInfo.InvariantCulture, $"Notification title cannot exceed {PushNotificationInvariants.TitleMaxLength} characters."));

        RuleFor(x => x.Body)
            .NotEmpty().WithMessage("Notification body is required.")
            .MaximumLength(PushNotificationInvariants.BodyMaxLength)
            .WithMessage(string.Create(CultureInfo.InvariantCulture, $"Notification body cannot exceed {PushNotificationInvariants.BodyMaxLength} characters."));

        // The scope key stays optional: a null one is the unscoped send every existing caller makes.
        RuleFor(x => x.ScopeKey)
            .MaximumLength(PushNotification.ScopeKeyMaxLength)
            .WithMessage(string.Create(CultureInfo.InvariantCulture, $"Notification scope key cannot exceed {PushNotification.ScopeKeyMaxLength} characters."));
    }
}
