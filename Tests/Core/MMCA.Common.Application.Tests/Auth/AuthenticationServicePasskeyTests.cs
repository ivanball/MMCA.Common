using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// The shared workflow's passkey members (ADR-139): a verified assertion mints the normal token pair
/// with <c>mfa=passkey</c>, a refresh carries that value over like <c>otp</c>, the app's login gate
/// still applies, and the last-passkey question answers from the stored password (and the app's
/// external-login hook). Plus the recent-sign-in rule registration runs on.
/// </summary>
public sealed class AuthenticationServicePasskeyTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SignInWithPasskeyAsync_MintsTheMultiFactorClaimNamingPasskey_AndASession()
    {
        var harness = new Harness();

        Result<AuthenticationResponse> result = await harness.Sut.SignInWithPasskeyAsync(1);

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().ContainSingle(c =>
            c.Type == AuthClaimTypes.MultiFactor && c.Value == AuthClaimTypes.MultiFactorMethodPasskey);
        harness.CapturedClaims.Should().Contain(c => c.Type == AuthClaimTypes.SessionId);
        harness.Sessions.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task SignInWithPasskeyAsync_ForAVanishedAccount_FailsWithTheGenericAssertionError()
    {
        var harness = new Harness();

        Result<AuthenticationResponse> result = await harness.Sut.SignInWithPasskeyAsync(404);

        result.Errors.Should().ContainSingle(e => e.Code == PasskeyErrors.AssertionFailedCode);
        harness.Sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SignInWithPasskeyAsync_DoesNotLeaveTheClaimArmedForTheNextMint()
    {
        var harness = new Harness();
        await harness.Sut.SignInWithPasskeyAsync(1);

        await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        harness.CapturedClaims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    [Fact]
    public async Task RefreshTokenAsync_CarriesThePasskeyMultiFactorClaimOntoTheRotatedToken()
    {
        var harness = new Harness();
        harness.SeedSession("stored-refresh");
        harness.ArrangeRefreshPrincipal(new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodPasskey));

        Result<AuthenticationResponse> result = await harness.Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().ContainSingle(c =>
            c.Type == AuthClaimTypes.MultiFactor && c.Value == AuthClaimTypes.MultiFactorMethodPasskey);
    }

    [Fact]
    public async Task HasSignInMethodBesidesPasskeysAsync_WithAStoredPassword_IsTrue()
    {
        var harness = new Harness(passwordHash: [1, 2, 3]);

        (await harness.Sut.HasSignInMethodBesidesPasskeysAsync(1)).Should().BeTrue();
    }

    [Fact]
    public async Task HasSignInMethodBesidesPasskeysAsync_WithNoPasswordAndNoExternalLogin_IsFalse()
    {
        var harness = new Harness(passwordHash: []);

        (await harness.Sut.HasSignInMethodBesidesPasskeysAsync(1)).Should().BeFalse();
    }

    // == The recent-sign-in rule for registering a passkey ==
    [Fact]
    public void StepUp_ATokenIssuedNineMinutesAgo_IsFresh() =>
        PasskeyStepUp.IsSatisfiedBy(Principal(issuedAgo: TimeSpan.FromMinutes(9)), FixedNow).Should().BeTrue();

    [Fact]
    public void StepUp_ATokenIssuedTwentyMinutesAgoWithoutMfa_IsRefused() =>
        PasskeyStepUp.IsSatisfiedBy(Principal(issuedAgo: TimeSpan.FromMinutes(20)), FixedNow).Should().BeFalse();

    [Fact]
    public void StepUp_ATokenIssuedTwentyMinutesAgoWithMfa_IsAllowed() =>
        PasskeyStepUp.IsSatisfiedBy(
            Principal(issuedAgo: TimeSpan.FromMinutes(20), new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp)),
            FixedNow).Should().BeTrue();

    [Fact]
    public void StepUp_ATokenWithNoIssuedAtAndNoMfa_IsRefused() =>
        PasskeyStepUp.IsSatisfiedBy(new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, "1")])), FixedNow)
            .Should().BeFalse();

    private static ClaimsPrincipal Principal(TimeSpan issuedAgo, params Claim[] extra) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "1"),
            new Claim("iat", (FixedNow - issuedAgo).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            .. extra,
        ]));

    private sealed class Harness
    {
        public Harness(byte[]? passwordHash = null)
        {
            var user = new ConfirmableAuthUser { Id = 1, EmailConfirmed = true, PasswordHash = passwordHash ?? [1, 2, 3] };

            var repository = new Mock<IRepository<ConfirmableAuthUser, UserIdentifierType>>();
            repository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.GetRepository<ConfirmableAuthUser, UserIdentifierType>()).Returns(repository.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var issued = 0;
            TokenService.Setup(x => x.GenerateRefreshToken())
                .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));
            TokenService
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

            Sut = new ConfirmableAuthenticationService(
                unitOfWork.Object,
                TokenService.Object,
                passwordHasher.Object,
                loginProtection.Object,
                new FixedClock(FixedNow),
                new AuthenticationValidators(Valid<LoginRequest>(), Valid<RegisterRequest>(), Valid<RefreshTokenRequest>()),
                Sessions,
                Options.Create(new RefreshSessionSettings()),
                twoFactor: null,
                Options.Create(new EmailConfirmationSettings()))
            {
                UntrackedUser = user,
            };
        }

        public Mock<ITokenService> TokenService { get; } = new();

        public InMemoryRefreshSessionStore Sessions { get; } = new();

        public ConfirmableAuthenticationService Sut { get; }

        public IReadOnlyList<Claim> CapturedClaims { get; private set; } = [];

        public void SeedSession(string token) =>
            Sessions.Seed(RefreshSession.Create(1, token, FixedNow.UtcDateTime.AddMinutes(-5), FixedNow.UtcDateTime.AddDays(7)).Value!);

        public void ArrangeRefreshPrincipal(params Claim[] extraClaims)
        {
            List<Claim> claims = [new Claim(AuthClaimTypes.Subject, "1"), .. extraClaims];
            TokenService
                .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
                .Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        }

        private static IValidator<T> Valid<T>()
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
