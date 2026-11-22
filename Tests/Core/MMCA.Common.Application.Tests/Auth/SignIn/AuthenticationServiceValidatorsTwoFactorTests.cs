using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.SignIn;

/// <summary>
/// A host arms the sign-in second factor by registering the authenticator: the container puts it on
/// <see cref="AuthenticationValidators.TwoFactor"/>, and the service, whose own <c>twoFactor</c>
/// argument stays null, challenges through it. An authenticator passed to the service explicitly
/// still wins over the bundle's one.
/// </summary>
public sealed class AuthenticationServiceValidatorsTwoFactorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoginAsync_WithNoServiceArgument_AndAnEnrolledAccount_AsksForTheCodeThroughTheBundle()
    {
        var harness = new Harness(serviceTwoFactor: null, bundleTwoFactor: TwoFactorStub.Requiring());

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == AuthErrorCodes.TwoFactorRequired);
        harness.Sessions.Sessions.Should().BeEmpty("no session is opened before the factor is proved");
    }

    [Fact]
    public async Task LoginAsync_WithNoServiceArgument_AndAValidCode_MintsTheTimeBasedMultiFactorClaim()
    {
        var harness = new Harness(serviceTwoFactor: null, bundleTwoFactor: TwoFactorStub.Verifying(TwoFactorOutcome.VerifiedTotp));

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(
            new LoginRequest("user@example.com", "pw") { TwoFactorCode = "123456" });

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().ContainSingle(c =>
            c.Type == AuthClaimTypes.MultiFactor && c.Value == AuthClaimTypes.MultiFactorMethodTotp);
    }

    [Fact]
    public async Task LoginAsync_WithAnExplicitServiceArgument_UsesItInsteadOfTheBundles()
    {
        var harness = new Harness(
            serviceTwoFactor: TwoFactorStub.Verifying(TwoFactorOutcome.NotEnrolled),
            bundleTwoFactor: TwoFactorStub.Requiring());

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue("the authenticator the subclass passed explicitly decides, not the bundle's");
        harness.CapturedClaims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    [Fact]
    public async Task LoginAsync_WithNeitherAuthenticator_SignsInWithNoChallenge()
    {
        var harness = new Harness(serviceTwoFactor: null, bundleTwoFactor: null);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    private sealed class Harness
    {
        public Harness(ITwoFactorAuthenticator? serviceTwoFactor, ITwoFactorAuthenticator? bundleTwoFactor)
        {
            var user = new ConfirmableAuthUser { Id = 1, EmailConfirmed = true };
            var repository = new Mock<IRepository<ConfirmableAuthUser, UserIdentifierType>>();
            repository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.GetRepository<ConfirmableAuthUser, UserIdentifierType>()).Returns(repository.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var issued = 0;
            var tokenService = new Mock<ITokenService>();
            tokenService.Setup(x => x.GenerateRefreshToken())
                .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));
            tokenService
                .Setup(x => x.GenerateAccessToken(
                    It.IsAny<UserIdentifierType>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<Claim>?>()))
                .Returns((UserIdentifierType _, string _, string _, string _, IEnumerable<Claim>? claims) =>
                {
                    CapturedClaims = claims is null ? [] : [.. claims];
                    return "access-token";
                });

            var passwordHasher = new Mock<IPasswordHasher>();
            passwordHasher
                .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
                .Returns(true);

            var loginProtection = new Mock<ILoginProtectionService>();
            loginProtection
                .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            var validators = bundleTwoFactor is null
                ? new AuthenticationValidators(AlwaysValid<LoginRequest>(), AlwaysValid<RegisterRequest>(), AlwaysValid<RefreshTokenRequest>())
                : new AuthenticationValidators(
                    AlwaysValid<LoginRequest>(),
                    AlwaysValid<RegisterRequest>(),
                    AlwaysValid<RefreshTokenRequest>(),
                    legalAcceptance: null,
                    bundleTwoFactor);

            Sut = new ConfirmableAuthenticationService(
                unitOfWork.Object,
                tokenService.Object,
                passwordHasher.Object,
                loginProtection.Object,
                new FixedClock(FixedNow),
                validators,
                Sessions,
                Options.Create(new RefreshSessionSettings()),
                serviceTwoFactor,
                Options.Create(new EmailConfirmationSettings()))
            {
                UntrackedUser = user,
            };
        }

        public InMemoryRefreshSessionStore Sessions { get; } = new();

        public ConfirmableAuthenticationService Sut { get; }

        public IReadOnlyList<Claim> CapturedClaims { get; private set; } = [];

        private static IValidator<T> AlwaysValid<T>()
        {
            var validator = new Mock<IValidator<T>>();
            validator
                .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ValidationResult());
            return validator.Object;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
