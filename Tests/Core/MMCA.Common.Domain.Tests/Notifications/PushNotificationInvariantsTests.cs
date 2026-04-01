using AwesomeAssertions;
using MMCA.Common.Domain.Notifications.PushNotifications.Invariants;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Tests.Notifications;

public sealed class PushNotificationInvariantsTests
{
    private const string Source = "Test";

    // ── EnsureTitleIsValid ──
    [Fact]
    public void EnsureTitleIsValid_WithValidTitle_ReturnsSuccess()
    {
        Result result = PushNotificationInvariants.EnsureTitleIsValid("Valid Title", Source);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureTitleIsValid_WithEmptyOrWhitespace_ReturnsFailure(string? value)
    {
        Result result = PushNotificationInvariants.EnsureTitleIsValid(value!, Source);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Title.Empty");
    }

    [Fact]
    public void EnsureTitleIsValid_WithTitleExceedingMaxLength_ReturnsFailure()
    {
        string longTitle = new('x', PushNotificationInvariants.TitleMaxLength + 1);

        Result result = PushNotificationInvariants.EnsureTitleIsValid(longTitle, Source);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Title.TooLong");
    }

    [Fact]
    public void EnsureTitleIsValid_WithTitleAtMaxLength_ReturnsSuccess()
    {
        string title = new('x', PushNotificationInvariants.TitleMaxLength);

        Result result = PushNotificationInvariants.EnsureTitleIsValid(title, Source);

        result.IsSuccess.Should().BeTrue();
    }

    // ── EnsureBodyIsValid ──
    [Fact]
    public void EnsureBodyIsValid_WithValidBody_ReturnsSuccess()
    {
        Result result = PushNotificationInvariants.EnsureBodyIsValid("Valid body text.", Source);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureBodyIsValid_WithEmptyOrWhitespace_ReturnsFailure(string? value)
    {
        Result result = PushNotificationInvariants.EnsureBodyIsValid(value!, Source);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Body.Empty");
    }

    [Fact]
    public void EnsureBodyIsValid_WithBodyExceedingMaxLength_ReturnsFailure()
    {
        string longBody = new('x', PushNotificationInvariants.BodyMaxLength + 1);

        Result result = PushNotificationInvariants.EnsureBodyIsValid(longBody, Source);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Body.TooLong");
    }

    [Fact]
    public void EnsureBodyIsValid_WithBodyAtMaxLength_ReturnsSuccess()
    {
        string body = new('x', PushNotificationInvariants.BodyMaxLength);

        Result result = PushNotificationInvariants.EnsureBodyIsValid(body, Source);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Constants ──
    [Fact]
    public void TitleMaxLength_Is200() =>
        PushNotificationInvariants.TitleMaxLength.Should().Be(200);

    [Fact]
    public void BodyMaxLength_Is2000() =>
        PushNotificationInvariants.BodyMaxLength.Should().Be(2000);
}
