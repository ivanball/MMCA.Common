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
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// The <c>auth_time</c> claim (ADR-139): every interactive sign-in stamps the instant it happened, a
/// refresh carries that instant over UNCHANGED (unlike <c>iat</c>, which every refresh resets), and
/// passkey registration is judged by it alone: within ten minutes or refused, with no <c>mfa</c> or
/// token-age alternative, and refused outright for a token minted before the claim existed.
/// </summary>
public sealed class AuthTimeClaimTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static string Unix(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    // == Stamped at every interactive sign-in ==
    [Fact]
    public async Task PasswordSignIn_StampsAuthTimeAtTheSignInInstant()
    {
        var harness = new Harness();

        (await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"))).IsSuccess.Should().BeTrue();

        harness.AuthTime.Should().Be(Unix(FixedNow));
    }

    [Fact]
    public async Task PasskeySignIn_StampsAuthTimeAtTheSignInInstant()
    {
        var harness = new Harness();

        (await harness.Sut.SignInWithPasskeyAsync(1)).IsSuccess.Should().BeTrue();

        harness.AuthTime.Should().Be(Unix(FixedNow));
    }

    [Fact]
    public async Task SignInLinkRedeem_StampsAuthTimeAtTheSignInInstant()
    {
        var harness = new Harness();

        (await harness.Sut.SignInVerifiedEmailOwnerAsync(1)).IsSuccess.Should().BeTrue();

        harness.AuthTime.Should().Be(Unix(FixedNow));
    }

    // == Carried unchanged on refresh ==
    [Fact]
    public async Task Refresh_CarriesThePresentedAuthTimeUnchanged_ThoughTheTokenIsNew()
    {
        var harness = new Harness();
        harness.SeedSession("stored-refresh");
        var signedIn = FixedNow.AddMinutes(-20);
        harness.ArrangeRefreshPrincipal(
            new Claim(AuthClaimTypes.AuthTime, Unix(signedIn)),
            new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodPasskey));

        var result = await harness.Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        harness.AuthTime.Should().Be(Unix(signedIn), "a refresh is not a sign-in, so the session keeps its sign-in instant");
    }

    [Fact]
    public async Task Refresh_OfATokenWithNoAuthTime_MintsNone()
    {
        var harness = new Harness();
        harness.SeedSession("stored-refresh");
        harness.ArrangeRefreshPrincipal();

        (await harness.Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "stored-refresh"))).IsSuccess.Should().BeTrue();

        harness.AuthTime.Should().BeNull("a refresh must never invent a sign-in instant for a token minted before the claim");
    }

    // == The registration rule ==
    [Fact]
    public void StepUp_ASignInFiveMinutesAgo_CanRegister() =>
        PasskeyStepUp.IsSatisfiedBy(Principal(signedInAgo: TimeSpan.FromMinutes(5)), FixedNow).Should().BeTrue();

    [Fact]
    public void StepUp_ASignInTwentyMinutesAgo_CannotRegister_EvenWithABrandNewTokenAndMfa() =>
        PasskeyStepUp.IsSatisfiedBy(
            Principal(
                signedInAgo: TimeSpan.FromMinutes(20),
                new Claim("iat", Unix(FixedNow)),
                new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp)),
            FixedNow).Should().BeFalse();

    [Fact]
    public void StepUp_ATokenWithNoAuthTime_CannotRegister_EvenWhenFreshAndCarryingMfa() =>
        PasskeyStepUp.IsSatisfiedBy(
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, "1"),
                new Claim("iat", Unix(FixedNow)),
                new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp),
            ])),
            FixedNow).Should().BeFalse();

    [Fact]
    public void StepUp_ReadsTheMappedClaimName_TheJwtBearerHandlerProduces() =>
        PasskeyStepUp.IsSatisfiedBy(
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, "1"),
                new Claim(ClaimTypes.AuthenticationInstant, Unix(FixedNow.AddMinutes(-2))),
            ])),
            FixedNow).Should().BeTrue();

    private static ClaimsPrincipal Principal(TimeSpan signedInAgo, params Claim[] extra) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "1"),
            new Claim(AuthClaimTypes.AuthTime, Unix(FixedNow - signedInAgo)),
            .. extra,
        ]));

    private sealed class Harness
    {
        public Harness()
        {
            var user = new ConfirmableAuthUser { Id = 1, EmailConfirmed = true };

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

        public string? AuthTime => CapturedClaims.SingleOrDefault(c => c.Type == AuthClaimTypes.AuthTime)?.Value;

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
