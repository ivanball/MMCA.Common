using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Shared.Notifications;

namespace MMCA.Common.Shared.Tests.Notifications;

/// <summary>
/// The formatter and the pattern that guards it ship together so a change to either half cannot
/// silently strand the other. Every key <see cref="NotificationScopeKey.ForEvent"/> and
/// <see cref="NotificationScopeKey.ForSession"/> produce must satisfy
/// <see cref="NotificationScopeKey.Pattern"/>, which is the default of
/// <c>PushNotificationSettings.ChannelKeyPattern</c>, the regex the notification hub enforces.
/// </summary>
public sealed class NotificationScopeKeyTests
{
    [Fact]
    public void ForEvent_ProducesThePrefixedKey() =>
        NotificationScopeKey.ForEvent(42).Should().Be("event:42");

    [Fact]
    public void ForSession_ProducesThePrefixedKey() =>
        NotificationScopeKey.ForSession(7).Should().Be("session:7");

    [Fact]
    public void Pattern_MatchesTheChannelKeyPatternDefault() =>
        NotificationScopeKey.Pattern.Should().Be("^(event|session):[0-9]+$");

    [Fact]
    public void Prefixes_MatchTheAlternationInThePattern()
    {
        NotificationScopeKey.EventPrefix.Should().Be("event");
        NotificationScopeKey.SessionPrefix.Should().Be("session");
    }

    // ── Format and validation cannot drift: everything the formatter emits validates ──
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2147483647L)]
    public void ForEvent_AlwaysProducesAValidKey(long eventId) =>
        NotificationScopeKey.IsValid(NotificationScopeKey.ForEvent(eventId)).Should().BeTrue();

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2147483647L)]
    public void ForSession_AlwaysProducesAValidKey(long sessionId) =>
        NotificationScopeKey.IsValid(NotificationScopeKey.ForSession(sessionId)).Should().BeTrue();

    [Fact]
    public void ForEvent_FormatsTheIdentifierInvariantly()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            NotificationScopeKey.ForEvent(1234).Should().Be(
                "event:1234",
                "a culture with its own digit shapes would otherwise produce a key the pattern rejects");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── IsValid ──
    [Theory]
    [InlineData("event:1")]
    [InlineData("session:100")]
    public void IsValid_WithWellFormedKey_ReturnsTrue(string scopeKey) =>
        NotificationScopeKey.IsValid(scopeKey).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("event:")]
    [InlineData("event:abc")]
    [InlineData("sponsor:1")]
    [InlineData("Event:1")]
    [InlineData("event:1 ")]
    [InlineData("event:-1")]
    public void IsValid_WithMalformedKey_ReturnsFalse(string? scopeKey) =>
        NotificationScopeKey.IsValid(scopeKey).Should().BeFalse();
}
