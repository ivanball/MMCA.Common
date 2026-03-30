using FluentValidation;
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
            .WithMessage($"Notification title cannot exceed {PushNotificationInvariants.TitleMaxLength} characters.");

        RuleFor(x => x.Body)
            .NotEmpty().WithMessage("Notification body is required.")
            .MaximumLength(PushNotificationInvariants.BodyMaxLength)
            .WithMessage($"Notification body cannot exceed {PushNotificationInvariants.BodyMaxLength} characters.");
    }
}
