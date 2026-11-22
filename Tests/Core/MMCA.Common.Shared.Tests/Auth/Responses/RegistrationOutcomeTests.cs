using AwesomeAssertions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Shared.Tests.Auth.Responses;

/// <summary>
/// The in-process signal for "registered, confirmation pending, no session": a response without an
/// access token is the pending outcome, and any response that carries one opened a session.
/// </summary>
public sealed class RegistrationOutcomeTests
{
    [Fact]
    public void IsConfirmationPending_ForTheConfirmationPendingValue_IsTrue() =>
        RegistrationOutcome.ConfirmationPending.IsConfirmationPending().Should().BeTrue();

    [Fact]
    public void IsConfirmationPending_ForTheDefaultResponse_IsTrue() =>
        default(AuthenticationResponse).IsConfirmationPending().Should().BeTrue(
            "a response with no access token opened no session, whatever produced it");

    [Fact]
    public void IsConfirmationPending_ForARealTokenPair_IsFalse() =>
        new AuthenticationResponse("access-token", "refresh-token", DateTime.UtcNow.AddMinutes(15))
            .IsConfirmationPending().Should().BeFalse();

    [Fact]
    public void ConfirmationPending_CarriesNoToken()
    {
        var pending = RegistrationOutcome.ConfirmationPending;

        pending.AccessToken.Should().BeEmpty();
        pending.RefreshToken.Should().BeEmpty();
    }

    [Fact]
    public void RegistrationPendingResponse_CarriesTheAddress() =>
        new RegistrationPendingResponse("new@example.com").Email.Should().Be("new@example.com");

    // The wire values the server answers with; a client branches on these strings.
    [Theory]
    [InlineData(AuthErrorCodes.MultiFactorRequired, "Authorization.MultiFactorRequired")]
    [InlineData(AuthErrorCodes.TwoFactorRequired, "Authentication.TwoFactorRequired")]
    [InlineData(AuthErrorCodes.TwoFactorInvalid, "Authentication.TwoFactorInvalid")]
    [InlineData(AuthErrorCodes.EmailNotConfirmed, "Authentication.EmailNotConfirmed")]
    public void AuthErrorCodes_HaveTheServerWireValues(string constant, string expected) =>
        constant.Should().Be(expected);
}
