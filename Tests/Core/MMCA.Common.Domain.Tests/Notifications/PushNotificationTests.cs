using AwesomeAssertions;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.PushNotifications.DomainEvents;

namespace MMCA.Common.Domain.Tests.Notifications;

public class PushNotificationTests
{
    // -- Create --
    [Fact]
    public void Create_WithValidData_ReturnsSuccess()
    {
        var result = PushNotification.Create("Test Title", "Test Body", sentByUserId: 1, recipientCount: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Title.Should().Be("Test Title");
        result.Value.Body.Should().Be("Test Body");
        result.Value.SentByUserId.Should().Be(1);
        result.Value.RecipientCount.Should().Be(50);
        result.Value.Status.Should().Be(PushNotificationStatus.Pending);
    }

    [Fact]
    public void Create_WithEmptyTitle_ReturnsFailure()
    {
        var result = PushNotification.Create(string.Empty, "Test Body", sentByUserId: 1, recipientCount: 10);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Title.Empty");
    }

    [Fact]
    public void Create_WithEmptyBody_ReturnsFailure()
    {
        var result = PushNotification.Create("Test Title", string.Empty, sentByUserId: 1, recipientCount: 10);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Body.Empty");
    }

    [Fact]
    public void Create_WithTitleExceedingMaxLength_ReturnsFailure()
    {
        string longTitle = new('x', 201);
        var result = PushNotification.Create(longTitle, "Test Body", sentByUserId: 1, recipientCount: 10);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Title.TooLong");
    }

    [Fact]
    public void Create_WithBodyExceedingMaxLength_ReturnsFailure()
    {
        string longBody = new('x', 2001);
        var result = PushNotification.Create("Test Title", longBody, sentByUserId: 1, recipientCount: 10);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "PushNotification.Body.TooLong");
    }

    [Fact]
    public void Create_RaisesPushNotificationCreatedEvent()
    {
        var result = PushNotification.Create("Test Title", "Test Body", sentByUserId: 1, recipientCount: 50);

        result.Value!.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PushNotificationCreated>()
            .Which.Title.Should().Be("Test Title");
    }

    [Fact]
    public void Create_WithMultipleInvalidFields_ReturnsAllErrors()
    {
        var result = PushNotification.Create(string.Empty, string.Empty, sentByUserId: 1, recipientCount: 10);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // -- Status transitions --
    [Fact]
    public void MarkAsSent_SetsStatusToSent()
    {
        PushNotification notification = CreateNotification();

        notification.MarkAsSent();

        notification.Status.Should().Be(PushNotificationStatus.Sent);
    }

    [Fact]
    public void MarkAsFailed_SetsStatusToFailed()
    {
        PushNotification notification = CreateNotification();

        notification.MarkAsFailed();

        notification.Status.Should().Be(PushNotificationStatus.Failed);
    }

    // -- Helpers --
    private static PushNotification CreateNotification() =>
        PushNotification.Create("Test Title", "Test Body", sentByUserId: 1, recipientCount: 50).Value!;
}
