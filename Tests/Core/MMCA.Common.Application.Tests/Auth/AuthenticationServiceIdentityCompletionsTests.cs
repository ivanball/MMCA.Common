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
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Exercises the two sign-in gates the identity completions add to the shared workflow: the
/// email-confirmation refusal and the second-factor challenge, plus the <c>mfa</c> claim the issued
/// token carries and the way a refresh carries it across a rotation.
/// </summary>
/// <remarks>
/// Both gates are opt-in through OPTIONAL constructor arguments, so the first test here is the one
/// that matters most: an app that passes neither sees the workflow it always had.
/// </remarks>
public sealed class AuthenticationServiceIdentityCompletionsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    // ── The unadopted path is unchanged ──
    [Fact]
    public async Task LoginAsync_WithNeitherFeatureWired_SignsInAndMintsNoMultiFactorClaim()
    {
        var harness = new Harness();

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    // ── Email confirmation gate ──
    [Fact]
    public async Task LoginAsync_WithConfirmationRequiredAndAnUnconfirmedAddress_IsRefused()
    {
        var harness = new Harness(requireConfirmedEmail: true, emailConfirmed: false);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == EmailConfirmationErrors.EmailNotConfirmedCode && e.Type == ErrorType.Unauthorized);
    }

    [Fact]
    public async Task LoginAsync_WithConfirmationRequiredAndAConfirmedAddress_SignsIn()
    {
        var harness = new Harness(requireConfirmedEmail: true, emailConfirmed: true);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_WithTheFlagOffAndAnUnconfirmedAddress_SignsIn()
    {
        // Adopting the token workflow must not change who can sign in until the host says so.
        var harness = new Harness(requireConfirmedEmail: false, emailConfirmed: false);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_WithAWrongPassword_IsRefusedBeforeTheConfirmationGateIsReadable()
    {
        // The gate names a real account state, so it must sit behind the password check.
        var harness = new Harness(requireConfirmedEmail: true, emailConfirmed: false, passwordVerifies: false);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidCredentials");
    }

    // ── Second-factor challenge ──
    [Fact]
    public async Task LoginAsync_WithAnEnrolledAccountAndNoCode_AsksForTheSecondFactor()
    {
        var harness = new Harness(twoFactor: TwoFactorStub.Requiring());

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorRequiredCode);
        harness.Sessions.Saved.Should().BeEmpty("no session is opened for a sign-in that never completed");
    }

    [Fact]
    public async Task LoginAsync_WithAWrongCode_IsRefused()
    {
        var harness = new Harness(twoFactor: TwoFactorStub.Rejecting());

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(
            new LoginRequest("user@example.com", "pw") { TwoFactorCode = "000000" });

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode);
    }

    [Fact]
    public async Task LoginAsync_WithAValidTimeBasedCode_MintsTheMultiFactorClaim()
    {
        var harness = new Harness(twoFactor: TwoFactorStub.Verifying(TwoFactorOutcome.VerifiedTotp));

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(
            new LoginRequest("user@example.com", "pw") { TwoFactorCode = "123456" });

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().ContainSingle(c =>
            c.Type == AuthClaimTypes.MultiFactor && c.Value == AuthClaimTypes.MultiFactorMethodTotp);
    }

    [Fact]
    public async Task LoginAsync_WithARecoveryCode_MintsTheMultiFactorClaimNamingThatMethod()
    {
        var harness = new Harness(twoFactor: TwoFactorStub.Verifying(TwoFactorOutcome.VerifiedRecoveryCode));

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(
            new LoginRequest("user@example.com", "pw") { TwoFactorCode = "recovery" });

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().ContainSingle(c =>
            c.Type == AuthClaimTypes.MultiFactor && c.Value == AuthClaimTypes.MultiFactorMethodRecoveryCode);
    }

    [Fact]
    public async Task LoginAsync_WithAnAccountThatNeverEnrolled_MintsNoMultiFactorClaim()
    {
        var harness = new Harness(twoFactor: TwoFactorStub.Verifying(TwoFactorOutcome.NotEnrolled));

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    [Fact]
    public async Task LoginAsync_StillCarriesTheSessionClaimAlongsideTheMultiFactorClaim()
    {
        // The two stamps share one wrapper, so it is worth pinning that adding the second did not
        // displace the first.
        var harness = new Harness(twoFactor: TwoFactorStub.Verifying(TwoFactorOutcome.VerifiedTotp));

        await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw") { TwoFactorCode = "123456" });

        harness.CapturedClaims.Should().Contain(c => c.Type == AuthClaimTypes.SessionId);
        harness.CapturedClaims.Should().Contain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    // ── Refresh carries the step-up across a rotation ──
    [Fact]
    public async Task RefreshTokenAsync_CarriesTheMultiFactorClaimOntoTheRotatedToken()
    {
        var harness = new Harness();
        harness.SeedSession("stored-refresh");
        harness.ArrangeRefreshPrincipal(new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp));

        Result<AuthenticationResponse> result = await harness.Sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().ContainSingle(c =>
            c.Type == AuthClaimTypes.MultiFactor && c.Value == AuthClaimTypes.MultiFactorMethodTotp,
            "dropping it would silently demote a signed-in session every access-token lifetime");
    }

    [Fact]
    public async Task RefreshTokenAsync_WithNoMultiFactorClaimOnThePresentedToken_MintsNone()
    {
        var harness = new Harness();
        harness.SeedSession("stored-refresh");
        harness.ArrangeRefreshPrincipal();

        Result<AuthenticationResponse> result = await harness.Sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        harness.CapturedClaims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor);
    }

    /// <summary>Wires the shared workflow with the collaborators one test needs and captures the minted claims.</summary>
    private sealed class Harness
    {
        private readonly Mock<IRepository<ConfirmableAuthUser, UserIdentifierType>> _repository = new();

        public Harness(
            bool requireConfirmedEmail = false,
            bool emailConfirmed = true,
            bool passwordVerifies = true,
            ITwoFactorAuthenticator? twoFactor = null)
        {
            var user = new ConfirmableAuthUser { Id = 1, EmailConfirmed = emailConfirmed };

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(x => x.GetRepository<ConfirmableAuthUser, UserIdentifierType>()).Returns(_repository.Object);
            unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            _repository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);

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
                .Returns(passwordVerifies);

            var loginProtection = new Mock<ILoginProtectionService>();
            loginProtection
                .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            var validators = new AuthenticationValidators(
                AlwaysValid<LoginRequest>(),
                AlwaysValid<RegisterRequest>(),
                AlwaysValid<RefreshTokenRequest>());

            Sut = new ConfirmableAuthenticationService(
                unitOfWork.Object,
                TokenService.Object,
                passwordHasher.Object,
                loginProtection.Object,
                new FixedClock(FixedNow),
                validators,
                Sessions,
                Options.Create(new RefreshSessionSettings()),
                twoFactor,
                Options.Create(new EmailConfirmationSettings { RequireConfirmedEmail = requireConfirmedEmail }))
            {
                UntrackedUser = user,
            };
        }

        public Mock<ITokenService> TokenService { get; } = new();

        public FakeRefreshSessionStore Sessions { get; } = new();

        public ConfirmableAuthenticationService Sut { get; }

        public IReadOnlyList<Claim> CapturedClaims { get; private set; } = [];

        public void SeedSession(string token) =>
            Sessions.Seed(RefreshSession.Create(
                1,
                token,
                FixedNow.UtcDateTime.AddMinutes(-5),
                FixedNow.UtcDateTime.AddDays(7)).Value!);

        public void ArrangeRefreshPrincipal(params Claim[] extraClaims)
        {
            List<Claim> claims = [new Claim(AuthClaimTypes.Subject, "1"), .. extraClaims];
            TokenService
                .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
                .Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        }

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

/// <summary>A user aggregate that also carries the email-confirmation contract.</summary>
public sealed class ConfirmableAuthUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser, IEmailConfirmableUser
{
    public byte[] PasswordHash { get; set; } = [1, 2, 3];

    public byte[] PasswordSalt { get; set; } = [4, 5, 6];

    public bool EmailConfirmed { get; set; }

    public bool IsEmailConfirmed => EmailConfirmed;

    public Result ConfirmEmail()
    {
        EmailConfirmed = true;
        return Result.Success();
    }
}

/// <summary>
/// Concrete subclass that mints its access token THROUGH the base's token service, which is what
/// makes the claims the workflow stamps observable.
/// </summary>
public sealed class ConfirmableAuthenticationService(
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    TimeProvider timeProvider,
    AuthenticationValidators validators,
    IRefreshSessionStore refreshSessions,
    IOptions<RefreshSessionSettings> refreshSessionSettings,
    ITwoFactorAuthenticator? twoFactor,
    IOptions<EmailConfirmationSettings>? emailConfirmationSettings)
    : AuthenticationServiceBase<ConfirmableAuthUser>(
        unitOfWork,
        tokenService,
        passwordHasher,
        loginProtection,
        timeProvider,
        validators,
        refreshSessions,
        refreshSessionSettings,
        twoFactor,
        emailConfirmationSettings)
{
    public ConfirmableAuthUser? UntrackedUser { get; set; }

    protected override Task<ConfirmableAuthUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(UntrackedUser);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    protected override Result<ConfirmableAuthUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        Result.Success(new ConfirmableAuthUser { Id = 1 });

    protected override string CreateAccessToken(ConfirmableAuthUser user) =>
        TokenService.GenerateAccessToken(user.Id, "user@example.com", "Attendee", "Test User");
}

/// <summary>Canned <see cref="ITwoFactorAuthenticator"/> answers for the sign-in gate tests.</summary>
internal sealed class TwoFactorStub(Result<TwoFactorOutcome> answer) : ITwoFactorAuthenticator
{
    public static TwoFactorStub Requiring() =>
        new(Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorRequired()));

    public static TwoFactorStub Rejecting() =>
        new(Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorInvalid()));

    public static TwoFactorStub Verifying(TwoFactorOutcome outcome) => new(Result.Success(outcome));

    public Task<Result<TwoFactorOutcome>> ChallengeAsync(
        UserIdentifierType userId,
        string? code,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(answer);
}
