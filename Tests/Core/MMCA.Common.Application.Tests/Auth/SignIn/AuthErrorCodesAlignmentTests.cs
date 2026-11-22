using AwesomeAssertions;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Tests.Auth.SignIn;

/// <summary>
/// The client-facing constants in <see cref="AuthErrorCodes"/> must name exactly the codes the server
/// emits, or a client that branches on them never sees the case it handles. This project sees both
/// sides, so it is where the two are compared.
/// </summary>
public sealed class AuthErrorCodesAlignmentTests
{
    [Fact]
    public void TwoFactorRequired_EqualsTheCodeTheSignInChallengeEmits()
    {
        AuthErrorCodes.TwoFactorRequired.Should().Be(TwoFactorErrors.TwoFactorRequiredCode);
        TwoFactorErrors.TwoFactorRequired().Code.Should().Be(AuthErrorCodes.TwoFactorRequired);
    }

    [Fact]
    public void TwoFactorInvalid_EqualsTheCodeAWrongCodeEmits()
    {
        AuthErrorCodes.TwoFactorInvalid.Should().Be(TwoFactorErrors.TwoFactorInvalidCode);
        TwoFactorErrors.TwoFactorInvalid().Code.Should().Be(AuthErrorCodes.TwoFactorInvalid);
    }

    [Fact]
    public void EmailNotConfirmed_EqualsTheCodeTheConfirmationGateEmits()
    {
        AuthErrorCodes.EmailNotConfirmed.Should().Be(EmailConfirmationErrors.EmailNotConfirmedCode);
        EmailConfirmationErrors.EmailNotConfirmed().Code.Should().Be(AuthErrorCodes.EmailNotConfirmed);
    }

    // The CQRS gate's refusal is pinned by AuthorizationMultiFactorDecoratorTests against the literal;
    // this pins that the shared constant carries the same literal.
    [Fact]
    public void MultiFactorRequired_EqualsTheCodeThePermissionGatesEmit() =>
        AuthErrorCodes.MultiFactorRequired.Should().Be("Authorization.MultiFactorRequired");
}
