using AwesomeAssertions;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.SignIn;

/// <summary>
/// Pins security review 2026-11-21 item A5-03: the shared issuance a non-password sign-in mints through
/// (an app-level external login, a sign-in link) honors an enrolled second factor the way password
/// sign-in does, so proving the identity provider or the mailbox alone never skips the factor. A host
/// that configured no two-factor issues exactly as before.
/// </summary>
public sealed class AuthenticationServiceSecondFactorIssuanceTests
{
    private const UserIdentifierType UserId = 52;

    [Fact]
    public async Task ExternalLogin_ForAnEnrolledAccount_GetsTheSecondFactorChallengeAndNoSession()
    {
        var harness = new SignInHarness<SignInTestUser>(EnrolledTwoFactor().Object);
        var user = new SignInTestUser { Id = UserId };
        harness.Arrange(user);

        Result<AuthenticationResponse> result = await harness.Sut.SignInExternallyAsync(user);

        result.IsFailure.Should().BeTrue("an external login proves the provider account, not the enrolled factor");
        result.Errors.Should().Contain(e => e.Code == TwoFactorErrors.TwoFactorRequiredCode);
        harness.Sessions.Sessions.Should().BeEmpty("no token pair may be issued before the factor is proved");
    }

    [Fact]
    public async Task ExternalLogin_ForAnAccountThatNeverEnrolled_IssuesTokens()
    {
        var twoFactor = new Mock<ITwoFactorAuthenticator>();
        twoFactor
            .Setup(x => x.ChallengeAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(TwoFactorOutcome.NotEnrolled));
        var harness = new SignInHarness<SignInTestUser>(twoFactor.Object);
        var user = new SignInTestUser { Id = UserId };
        harness.Arrange(user);

        Result<AuthenticationResponse> result = await harness.Sut.SignInExternallyAsync(user);

        result.IsSuccess.Should().BeTrue();
        harness.Sessions.Sessions.Should().ContainSingle().Which.UserId.Should().Be(UserId);
    }

    [Fact]
    public async Task ExternalLogin_OnAHostWithNoTwoFactorConfigured_IssuesTokensUnchanged()
    {
        var harness = new SignInHarness<SignInTestUser>(twoFactor: null);
        var user = new SignInTestUser { Id = UserId };
        harness.Arrange(user);

        Result<AuthenticationResponse> result = await harness.Sut.SignInExternallyAsync(user);

        result.IsSuccess.Should().BeTrue("a host that has not adopted two-factor keeps its external login as it was");
        result.Value.AccessToken.Should().Be("access-52");
        harness.Sessions.Sessions.Should().ContainSingle().Which.UserId.Should().Be(UserId);
    }

    [Fact]
    public async Task SignInLink_ForAnEnrolledAccount_IsRefusedAndOpensNoSession()
    {
        var harness = new SignInHarness<SignInTestUser>(EnrolledTwoFactor().Object);
        harness.Arrange(new SignInTestUser { Id = UserId });

        Result<AuthenticationResponse> result = await harness.Sut.SignInVerifiedEmailOwnerAsync(UserId, "10.0.0.1", "agent");

        result.IsFailure.Should().BeTrue("a sign-in link proves the mailbox, not the enrolled factor");
        result.Errors.Should().ContainSingle(e => e.Code == SignInLinkErrors.SecondFactorRequiredCode);
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SignInLink_OnAHostWithNoTwoFactorConfigured_IssuesTokens()
    {
        var harness = new SignInHarness<SignInTestUser>(twoFactor: null);
        harness.Arrange(new SignInTestUser { Id = UserId });

        Result<AuthenticationResponse> result = await harness.Sut.SignInVerifiedEmailOwnerAsync(UserId, "10.0.0.1", "agent");

        result.IsSuccess.Should().BeTrue();
        harness.Sessions.Sessions.Should().ContainSingle();
    }

    // An enrolled account answers a challenge that carries no code the way the shipped authenticator
    // does: "supply your code".
    private static Mock<ITwoFactorAuthenticator> EnrolledTwoFactor()
    {
        var twoFactor = new Mock<ITwoFactorAuthenticator>();
        twoFactor
            .Setup(x => x.ChallengeAsync(UserId, It.Is<string?>(c => string.IsNullOrWhiteSpace(c)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorRequired()));
        return twoFactor;
    }
}
