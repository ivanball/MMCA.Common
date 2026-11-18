using AwesomeAssertions;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Infrastructure.Tests.Settings;

public class PushNotificationSettingsTests
{
    [Fact]
    public void SectionName_IsPushNotifications() =>
        PushNotificationSettings.SectionName.Should().Be("PushNotifications");

    [Theory]
    [InlineData(nameof(PushNotificationSettings.Enabled), false)]
    [InlineData(nameof(PushNotificationSettings.HubPath), "/hubs/notifications")]
    [InlineData(nameof(PushNotificationSettings.ChannelKeyPattern), "^(event|session):[0-9]+$")]
    public void Default_PropertyHasTheDocumentedValue(string property, object? expected) =>
        typeof(PushNotificationSettings).GetProperty(property)!.GetValue(new PushNotificationSettings()).Should().Be(expected);

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new PushNotificationSettings
        {
            Enabled = true,
            HubPath = "/custom/hub",
            ChannelKeyPattern = "^room:[a-z]+$",
        };

        sut.Enabled.Should().BeTrue();
        sut.HubPath.Should().Be("/custom/hub");
        sut.ChannelKeyPattern.Should().Be("^room:[a-z]+$");
    }
}
