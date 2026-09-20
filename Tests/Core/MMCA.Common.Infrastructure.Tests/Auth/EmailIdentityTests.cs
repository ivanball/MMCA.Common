using AwesomeAssertions;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Covers the one address normalization the auth services share for their cache keys: nothing
/// usable collapses to an empty key, a valid address reduces to the <c>Email</c> value object's own
/// normalized value, and a malformed one (which never matches a user, but can still mint a key)
/// still collapses onto a single trimmed, lowercased key rather than one key per spelling.
/// </summary>
public sealed class EmailIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Normalize_WithNothingUsable_ReturnsEmpty(string? email) =>
        EmailIdentity.Normalize(email).Should().BeEmpty();

    [Theory]
    [InlineData("User@Example.COM")]
    [InlineData("  User@Example.COM  ")]
    [InlineData("user@example.com")]
    public void Normalize_WithAValidAddress_ReturnsTheEmailValue(string email) =>
        EmailIdentity.Normalize(email).Should().Be("user@example.com");

    [Theory]
    [InlineData("  NotAnEmail  ", "notanemail")]
    [InlineData("User@", "user@")]
    [InlineData(" @Example.COM ", "@example.com")]
    public void Normalize_WithAMalformedAddress_FallsBackToTrimmedLowercase(string email, string expected) =>
        EmailIdentity.Normalize(email).Should().Be(expected);
}
