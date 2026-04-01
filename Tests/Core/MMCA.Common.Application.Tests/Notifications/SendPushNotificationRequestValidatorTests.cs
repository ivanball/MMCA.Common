using AwesomeAssertions;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Domain.Notifications.PushNotifications.Invariants;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class SendPushNotificationRequestValidatorTests
{
    private readonly SendPushNotificationRequestValidator _validator = new();

    // ── Title ──
    [Fact]
    public void Validate_WhenTitleEmpty_HasValidationError()
    {
        var request = new SendPushNotificationRequest(string.Empty, "Valid body");

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage("Notification title is required.");
    }

    [Fact]
    public void Validate_WhenTitleExceedsMaxLength_HasValidationError()
    {
        string longTitle = new('A', PushNotificationInvariants.TitleMaxLength + 1);
        var request = new SendPushNotificationRequest(longTitle, "Valid body");

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage($"Notification title cannot exceed {PushNotificationInvariants.TitleMaxLength} characters.");
    }

    [Fact]
    public void Validate_WhenTitleAtMaxLength_NoTitleError()
    {
        string maxTitle = new('A', PushNotificationInvariants.TitleMaxLength);
        var request = new SendPushNotificationRequest(maxTitle, "Valid body");

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.Title);
    }

    // ── Body ──
    [Fact]
    public void Validate_WhenBodyEmpty_HasValidationError()
    {
        var request = new SendPushNotificationRequest("Valid title", string.Empty);

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Body)
            .WithErrorMessage("Notification body is required.");
    }

    [Fact]
    public void Validate_WhenBodyExceedsMaxLength_HasValidationError()
    {
        string longBody = new('B', PushNotificationInvariants.BodyMaxLength + 1);
        var request = new SendPushNotificationRequest("Valid title", longBody);

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Body)
            .WithErrorMessage($"Notification body cannot exceed {PushNotificationInvariants.BodyMaxLength} characters.");
    }

    [Fact]
    public void Validate_WhenBodyAtMaxLength_NoBodyError()
    {
        string maxBody = new('B', PushNotificationInvariants.BodyMaxLength);
        var request = new SendPushNotificationRequest("Valid title", maxBody);

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.Body);
    }

    // ── Valid request ──
    [Fact]
    public void Validate_WhenValid_NoErrors()
    {
        var request = new SendPushNotificationRequest("Alert", "Something happened");

        TestValidationResult<SendPushNotificationRequest> result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
