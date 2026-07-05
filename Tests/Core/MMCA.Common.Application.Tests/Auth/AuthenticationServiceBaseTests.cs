using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.ValueObjects;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Exercises the shared authentication workflow through a concrete test subclass:
/// validate-first ordering, the ADR-029 lockout/rate-limit gates, the untracked-then-tracked
/// dual fetch, BR-205 token rotation and BR-206 refresh-token reuse detection.
/// </summary>
public sealed class AuthenticationServiceBaseTests
{
    private const string NewRefreshToken = "rotated-refresh-token";
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] HashedPassword = [9, 9, 9];
    private static readonly byte[] GeneratedSalt = [8, 8, 8];

    // ── LoginAsync ──
    [Fact]
    public async Task LoginAsync_WhenRequestInvalid_ReturnsValidationFailureWithoutLockoutCheck()
    {
        var (sut, mocks) = CreateSut(loginRequestValid: false);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("bad", string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().OnlyContain(e => e.Type == ErrorType.Validation);
        mocks.LoginProtection.Verify(
            x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenLockedOut_ReturnsLockoutFailureWithoutTouchingCredentials()
    {
        var (sut, mocks) = CreateSut();
        var lockoutError = Error.Unauthorized("Auth.TooManyAttempts", "Too many failed login attempts.");
        mocks.LoginProtection
            .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(lockoutError));
        sut.UntrackedUser = CreateTestUser(id: 1);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.TooManyAttempts");
        mocks.PasswordHasher.Verify(
            x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()),
            Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenEmailUnknown_ReturnsInvalidCredentialsAndIncrementsFailedAttempts()
    {
        var (sut, mocks) = CreateSut();
        sut.UntrackedUser = null;

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("unknown@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidCredentials" && e.Type == ErrorType.Unauthorized);
        mocks.LoginProtection.Verify(
            x => x.IncrementFailedAttemptsAsync("unknown@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenCandidateGateFails_ReturnsGateFailureWithoutIncrementOrPasswordCheck()
    {
        var (sut, mocks) = CreateSut();
        sut.UntrackedUser = CreateTestUser(id: 1);
        sut.LoginCandidateResult = Result.Failure(
            Error.Unauthorized("Auth.AccountDeactivated", "Account is deactivated."));

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.AccountDeactivated");
        mocks.PasswordHasher.Verify(
            x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()),
            Times.Never,
            "the app gate runs before password verification");
        mocks.LoginProtection.Verify(
            x => x.IncrementFailedAttemptsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a gate rejection is not a failed credential attempt");
    }

    [Fact]
    public async Task LoginAsync_WhenPasswordWrong_ReturnsInvalidCredentialsAndIncrementsFailedAttempts()
    {
        var (sut, mocks) = CreateSut();
        sut.UntrackedUser = CreateTestUser(id: 1);
        mocks.PasswordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(false);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "wrong"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidCredentials");
        mocks.LoginProtection.Verify(
            x => x.IncrementFailedAttemptsAsync("user@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenTrackedRefetchMissing_ReturnsNotFound()
    {
        var (sut, mocks) = CreateSut();
        sut.UntrackedUser = CreateTestUser(id: 1);
        mocks.Repository
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAuthUser?)null);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
    }

    [Fact]
    public async Task LoginAsync_WhenCredentialsValid_RotatesTokensAndResetsFailedAttempts()
    {
        var (sut, mocks) = CreateSut();
        sut.UntrackedUser = CreateTestUser(id: 1);
        var tracked = CreateTestUser(id: 1);
        mocks.Repository
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tracked);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-1");
        result.Value.RefreshToken.Should().Be(NewRefreshToken);
        result.Value.AccessTokenExpiry.Should().Be(FixedNow.UtcDateTime.AddMinutes(15), "BR-205 access-token lifetime");
        tracked.RefreshToken.Should().Be(NewRefreshToken, "the tracked instance persists the rotation");
        tracked.RefreshTokenExpiry.Should().Be(FixedNow.UtcDateTime.AddDays(7), "BR-205 refresh-token lifetime");
        mocks.LoginProtection.Verify(
            x => x.ResetFailedAttemptsAsync("user@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_NormalizesEmailToValueObjectBeforeLookup()
    {
        var (sut, mocks) = CreateSut();
        sut.UntrackedUser = null;

        await sut.LoginAsync(new LoginRequest("Mixed.Case@Example.COM", "pw"));

        sut.CapturedLoginEmail.Should().NotBeNull();
        sut.CapturedLoginEmail!.Value.Should().Be("mixed.case@example.com");
        mocks.LoginProtection.Verify(
            x => x.IncrementFailedAttemptsAsync("Mixed.Case@Example.COM", It.IsAny<CancellationToken>()),
            Times.Once,
            "the failed-attempt key uses the raw request email");
    }

    // ── RegisterAsync ──
    [Fact]
    public async Task RegisterAsync_WhenRequestInvalid_ReturnsValidationFailure()
    {
        var (sut, mocks) = CreateSut(registerRequestValid: false);

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("bad", string.Empty, "A", "B"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().OnlyContain(e => e.Type == ErrorType.Validation);
        mocks.LoginProtection.Verify(
            x => x.CheckRegistrationRateLimitAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenRateLimited_ReturnsRateLimitFailure()
    {
        var (sut, mocks) = CreateSut();
        mocks.LoginProtection
            .Setup(x => x.CheckRegistrationRateLimitAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Unauthorized("Auth.RegistrationRateLimitExceeded", "Too many registrations.")));

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B"), ipAddress: "10.0.0.1");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.RegistrationRateLimitExceeded");
        mocks.Repository.Verify(
            x => x.AddAsync(It.IsAny<TestAuthUser>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenEmailAlreadyExists_ReturnsConflict()
    {
        var (sut, mocks) = CreateSut();
        sut.EmailExists = true;

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("taken@example.com", "pw", "A", "B"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.EmailAlreadyExists" && e.Type == ErrorType.Conflict);
        mocks.Repository.Verify(
            x => x.AddAsync(It.IsAny<TestAuthUser>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenUserFactoryFails_ReturnsFactoryFailure()
    {
        var (sut, mocks) = CreateSut();
        sut.CreateUserResult = Result.Failure<TestAuthUser>(
            Error.Validation("User.InvalidName", "Name is invalid."));

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "User.InvalidName");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenValid_PersistsUserIssuesTokensAndCountsIp()
    {
        var (sut, mocks) = CreateSut();
        TestAuthUser? persisted = null;
        mocks.Repository
            .Setup(x => x.AddAsync(It.IsAny<TestAuthUser>(), It.IsAny<CancellationToken>()))
            .Callback<TestAuthUser, CancellationToken>((user, _) => persisted = user)
            .Returns(Task.CompletedTask);

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B"), ipAddress: "10.0.0.1");

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-77", "the token is minted from the registered user");
        result.Value.RefreshToken.Should().Be(NewRefreshToken);
        result.Value.AccessTokenExpiry.Should().Be(FixedNow.UtcDateTime.AddMinutes(15));

        persisted.Should().NotBeNull();
        persisted!.PasswordHash.Should().Equal(HashedPassword, "the factory receives the hasher output");
        persisted.PasswordSalt.Should().Equal(GeneratedSalt);
        persisted.RefreshToken.Should().Be(NewRefreshToken);
        persisted.RefreshTokenExpiry.Should().Be(FixedNow.UtcDateTime.AddDays(7));

        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        mocks.LoginProtection.Verify(
            x => x.IncrementRegistrationCountAsync("10.0.0.1", It.IsAny<CancellationToken>()),
            Times.Once,
            "BR-213 counts the registration against the caller's IP");
    }

    // ── RefreshTokenAsync ──
    [Fact]
    public async Task RefreshTokenAsync_WhenRequestInvalid_ReturnsValidationFailure()
    {
        var (sut, _) = CreateSut(refreshRequestValid: false);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest(string.Empty, string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().OnlyContain(e => e.Type == ErrorType.Validation);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenPrincipalInvalid_ReturnsInvalidToken()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken("tampered"))
            .Returns((ClaimsPrincipal?)null);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("tampered", "refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidToken" && e.Type == ErrorType.Unauthorized);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenUserIdClaimMissing_ReturnsInvalidTokenClaims()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal());

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidToken" && e.Message == "Invalid access token claims.");
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenUserMissing_ReturnsUnauthorizedByDefault()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(new Claim("user_id", "404")));
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAuthUser?)null);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidToken" && e.Type == ErrorType.Unauthorized,
            "a token for a vanished user is indistinguishable from an invalid token");
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenRefreshGateFails_ReturnsGateFailure()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        user.SeedRefreshToken("stored-refresh", FixedNow.UtcDateTime.AddDays(1));
        ArrangeRefreshFetch(mocks, user);
        sut.RefreshCandidateResult = Result.Failure(
            Error.Unauthorized("Auth.AccountDeactivated", "Account is deactivated."));

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.AccountDeactivated");
        user.RevokeCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTokenMismatch_RevokesStoredTokenAndFails()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        user.SeedRefreshToken("stored-refresh", FixedNow.UtcDateTime.AddDays(1));
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "reused-or-stolen"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        user.RevokeCalled.Should().BeTrue("BR-206 reuse detection revokes the stored token");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenStoredTokenExpired_RevokesAndFails()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        user.SeedRefreshToken("stored-refresh", FixedNow.UtcDateTime.AddSeconds(-1));
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        user.RevokeCalled.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTokenValid_RotatesPair()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        user.SeedRefreshToken("stored-refresh", FixedNow.UtcDateTime.AddDays(1));
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-1");
        result.Value.RefreshToken.Should().Be(NewRefreshToken);
        user.RefreshToken.Should().Be(NewRefreshToken, "BR-205 rotates the refresh token on use");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── RevokeTokenAsync ──
    [Fact]
    public async Task RevokeTokenAsync_WhenUserMissing_ReturnsNotFound()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAuthUser?)null);

        Result result = await sut.RevokeTokenAsync(404);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RevokeTokenAsync_WhenUserExists_RevokesAndSaves()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 5);
        user.SeedRefreshToken("stored-refresh", FixedNow.UtcDateTime.AddDays(1));
        mocks.Repository
            .Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Result result = await sut.RevokeTokenAsync(5);

        result.IsSuccess.Should().BeTrue();
        user.RevokeCalled.Should().BeTrue();
        user.RefreshToken.Should().BeNull();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ──
    private static TestAuthUser CreateTestUser(UserIdentifierType id) => new() { Id = id };

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static void ArrangeRefreshFetch(ServiceMocks mocks, TestAuthUser user)
    {
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(new Claim("user_id", user.Id.ToString(CultureInfo.InvariantCulture))));
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
    }

    private static Mock<IValidator<TRequest>> CreateValidatorMock<TRequest>(bool isValid)
    {
        var validator = new Mock<IValidator<TRequest>>();
        ValidationResult validationResult = isValid
            ? new ValidationResult()
            : new ValidationResult([new ValidationFailure("Property", "Property is invalid.")]);
        validator
            .Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);
        return validator;
    }

    private sealed record ServiceMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestAuthUser, UserIdentifierType>> Repository,
        Mock<ITokenService> TokenService,
        Mock<IPasswordHasher> PasswordHasher,
        Mock<ILoginProtectionService> LoginProtection);

    private static (TestAuthenticationService Sut, ServiceMocks Mocks) CreateSut(
        bool loginRequestValid = true,
        bool registerRequestValid = true,
        bool refreshRequestValid = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestAuthUser, UserIdentifierType>>();
        var tokenService = new Mock<ITokenService>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var loginProtection = new Mock<ILoginProtectionService>();

        unitOfWork
            .Setup(x => x.GetRepository<TestAuthUser, UserIdentifierType>())
            .Returns(repository.Object);
        unitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        tokenService.Setup(x => x.GenerateRefreshToken()).Returns(NewRefreshToken);

        passwordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        passwordHasher
            .Setup(x => x.HashPassword(It.IsAny<string>()))
            .Returns((HashedPassword, GeneratedSalt));

        loginProtection
            .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        loginProtection
            .Setup(x => x.CheckRegistrationRateLimitAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var validators = new AuthenticationValidators(
            CreateValidatorMock<LoginRequest>(loginRequestValid).Object,
            CreateValidatorMock<RegisterRequest>(registerRequestValid).Object,
            CreateValidatorMock<RefreshTokenRequest>(refreshRequestValid).Object);

        var sut = new TestAuthenticationService(
            unitOfWork.Object,
            tokenService.Object,
            passwordHasher.Object,
            loginProtection.Object,
            new FixedTimeProvider(FixedNow),
            validators);

        var mocks = new ServiceMocks(unitOfWork, repository, tokenService, passwordHasher, loginProtection);
        return (sut, mocks);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

/// <summary>
/// Minimal <c>User</c> aggregate standing in for an app's Identity user. Public (not nested)
/// because Moq must proxy <c>IRepository</c> closed over this type.
/// </summary>
public sealed class TestAuthUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
#pragma warning disable CA1819 // Properties should not return arrays: mirrors IAuthUser's byte[] password material (ADR-032).
    public byte[] PasswordHash { get; set; } = [1, 2, 3];

    public byte[] PasswordSalt { get; set; } = [4, 5, 6];
#pragma warning restore CA1819

    public string? RefreshToken { get; private set; }

    public DateTime? RefreshTokenExpiry { get; private set; }

    public bool RevokeCalled { get; private set; }

    public void UpdateRefreshToken(string refreshToken, DateTime expiry)
    {
        RefreshToken = refreshToken;
        RefreshTokenExpiry = expiry;
    }

    public void RevokeRefreshToken()
    {
        RevokeCalled = true;
        RefreshToken = null;
        RefreshTokenExpiry = null;
    }

    public void SeedRefreshToken(string? refreshToken, DateTime? expiry)
    {
        RefreshToken = refreshToken;
        RefreshTokenExpiry = expiry;
    }
}

/// <summary>Concrete subclass supplying the per-app hooks the shared workflow calls.</summary>
public sealed class TestAuthenticationService(
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    TimeProvider timeProvider,
    AuthenticationValidators validators)
    : AuthenticationServiceBase<TestAuthUser>(
        unitOfWork, tokenService, passwordHasher, loginProtection, timeProvider, validators)
{
    public TestAuthUser? UntrackedUser { get; set; }

    public bool EmailExists { get; set; }

    public Result<TestAuthUser>? CreateUserResult { get; set; }

    public Result LoginCandidateResult { get; set; } = Result.Success();

    public Result RefreshCandidateResult { get; set; } = Result.Success();

    public Email? CapturedLoginEmail { get; private set; }

    protected override Task<TestAuthUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken)
    {
        CapturedLoginEmail = email;
        return Task.FromResult(UntrackedUser);
    }

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(EmailExists);

    protected override Result<TestAuthUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        CreateUserResult ?? Result.Success(new TestAuthUser
        {
            Id = 77,
            PasswordHash = passwordHash,
            PasswordSalt = passwordSalt,
        });

    protected override string CreateAccessToken(TestAuthUser user) =>
        string.Create(CultureInfo.InvariantCulture, $"access-{user.Id}");

    protected override Task<Result> ValidateLoginCandidateAsync(TestAuthUser untrackedUser, CancellationToken cancellationToken) =>
        Task.FromResult(LoginCandidateResult);

    protected override Task<Result> ValidateRefreshCandidateAsync(TestAuthUser user, CancellationToken cancellationToken) =>
        Task.FromResult(RefreshCandidateResult);
}
