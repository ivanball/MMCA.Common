using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
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
/// Exercises the shared authentication workflow through a concrete test subclass:
/// validate-first ordering, the ADR-029 lockout/rate-limit gates, the untracked-then-tracked
/// dual fetch, and the multi-device refresh-session model (BR-205 rotation, BR-206 reuse detection,
/// hash-at-rest storage and the per-user session cap).
/// </summary>
public sealed class AuthenticationServiceBaseTests
{
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
    public async Task LoginAsync_WhenCredentialsValid_OpensASessionAndResetsFailedAttempts()
    {
        var (sut, mocks) = CreateSut();
        ArrangeLoginFetch(sut, mocks, CreateTestUser(id: 1));

        Result<AuthenticationResponse> result = await sut.LoginAsync(
            new LoginRequest("user@example.com", "pw"), ipAddress: "10.0.0.7", userAgent: "unit-test-agent");

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-1");
        result.Value.RefreshToken.Should().Be("refresh-1");
        result.Value.AccessTokenExpiry.Should().Be(FixedNow.UtcDateTime.AddMinutes(15), "BR-205 access-token lifetime");

        var session = mocks.Sessions.Saved.Should().ContainSingle().Subject;
        session.UserId.Should().Be(1);
        session.ExpiresAt.Should().Be(FixedNow.UtcDateTime.AddDays(7), "BR-205 refresh-token lifetime");
        session.CreatedAt.Should().Be(FixedNow.UtcDateTime);
        session.IsRevoked.Should().BeFalse();
        session.IpAddress.Should().Be("10.0.0.7");
        session.UserAgent.Should().Be("unit-test-agent");

        mocks.LoginProtection.Verify(
            x => x.ResetFailedAttemptsAsync("user@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
        mocks.Sessions.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task LoginAsync_StoresOnlyTheHashOfTheRefreshToken()
    {
        var (sut, mocks) = CreateSut();
        ArrangeLoginFetch(sut, mocks, CreateTestUser(id: 1));

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        var session = mocks.Sessions.Saved.Should().ContainSingle().Subject;
        session.TokenHash.Should().NotBe(result.Value.RefreshToken, "a stored plaintext token is a usable credential at rest");
        session.TokenHash.Should().Be(RefreshSession.HashToken(result.Value.RefreshToken));
        session.TokenHash.Should().HaveLength(RefreshSession.TokenHashLength);
    }

    [Fact]
    public async Task LoginAsync_LeavesTheUsersOtherDeviceSessionsAlone()
    {
        var (sut, mocks) = CreateSut();
        ArrangeLoginFetch(sut, mocks, CreateTestUser(id: 1));
        var phone = SeedSession(mocks, userId: 1, token: "phone-token");
        var laptop = SeedSession(mocks, userId: 1, token: "laptop-token");

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        phone.IsRevoked.Should().BeFalse("signing in on a new device must not sign the phone out");
        laptop.IsRevoked.Should().BeFalse();
        mocks.Sessions.Saved.Should().HaveCount(3);
    }

    [Fact]
    public async Task LoginAsync_WhenTheSessionCapIsFull_RevokesTheOldestSession()
    {
        var (sut, mocks) = CreateSut(maxActiveSessionsPerUser: 3);
        ArrangeLoginFetch(sut, mocks, CreateTestUser(id: 1));
        var oldest = SeedSession(mocks, userId: 1, token: "oldest", createdAt: FixedNow.UtcDateTime.AddDays(-3));
        var middle = SeedSession(mocks, userId: 1, token: "middle", createdAt: FixedNow.UtcDateTime.AddDays(-2));
        var newest = SeedSession(mocks, userId: 1, token: "newest", createdAt: FixedNow.UtcDateTime.AddDays(-1));

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue("the cap evicts rather than refusing a legitimate sign-in");
        oldest.IsRevoked.Should().BeTrue();
        oldest.ReasonRevoked.Should().Be(RefreshSession.ReasonSessionCap);
        middle.IsRevoked.Should().BeFalse();
        newest.IsRevoked.Should().BeFalse();
        mocks.Sessions.Saved.Count(s => !s.IsRevoked).Should().Be(3, "the cap holds after the new session opens");
    }

    [Fact]
    public async Task LoginAsync_WhenTokenServiceReportsConfiguredLifetimes_UsesThemForExpiries()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService.Setup(x => x.AccessTokenLifetime).Returns(TimeSpan.FromMinutes(30));
        mocks.TokenService.Setup(x => x.RefreshTokenLifetime).Returns(TimeSpan.FromDays(14));
        ArrangeLoginFetch(sut, mocks, CreateTestUser(id: 1));

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessTokenExpiry.Should().Be(
            FixedNow.UtcDateTime.AddMinutes(30),
            "Jwt:AccessTokenExpirationMinutes drives the reported expiry");
        mocks.Sessions.Saved.Should().ContainSingle()
            .Which.ExpiresAt.Should().Be(
                FixedNow.UtcDateTime.AddDays(14),
                "Jwt:RefreshTokenExpirationDays drives the stored session expiry");
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

    // The email check and the insert are a check-then-act: two concurrent registrations for the
    // same address both pass the check and the loser fails on the unique Email index. That used to
    // escape as a 500; it now resolves to the same conflict a serialized pair would have produced.
    [Fact]
    public async Task RegisterAsync_WhenAConcurrentRegistrationWinsTheUniqueIndex_ReturnsConflict()
    {
        var (sut, mocks) = CreateSut();
        mocks.UnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => sut.EmailExists = true)
            .ThrowsAsync(new InvalidOperationException("Cannot insert duplicate key row in index 'IX_Users_Email'."));

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("racer@example.com", "pw", "A", "B"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.EmailAlreadyExists" && e.Type == ErrorType.Conflict);
        sut.EmailExistsCallCount.Should().Be(
            2,
            "the failed save is classified by re-checking the address, since this layer cannot name the EF exception type");
    }

    [Fact]
    public async Task RegisterAsync_WhenTheSaveFailsForAnyOtherReason_Rethrows()
    {
        var (sut, mocks) = CreateSut();
        mocks.UnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection reset"));

        Func<Task> act = async () => await sut.RegisterAsync(
            new RegisterRequest("new@example.com", "pw", "A", "B"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("connection reset", "the broad catch classifies, it does not swallow");
        sut.EmailExistsCallCount.Should().Be(2);
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
    public async Task RegisterAsync_WhenValid_PersistsUserOpensASessionAndCountsIp()
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
        result.Value.RefreshToken.Should().Be("refresh-1");
        result.Value.AccessTokenExpiry.Should().Be(FixedNow.UtcDateTime.AddMinutes(15));

        persisted.Should().NotBeNull();
        persisted!.PasswordHash.Should().Equal(HashedPassword, "the factory receives the hasher output");
        persisted.PasswordSalt.Should().Equal(GeneratedSalt);

        var session = mocks.Sessions.Saved.Should().ContainSingle().Subject;
        session.UserId.Should().Be(77, "the session is opened after the insert assigns the id");
        session.TokenHash.Should().Be(RefreshSession.HashToken("refresh-1"));
        session.IpAddress.Should().Be("10.0.0.1");

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
    public async Task RefreshTokenAsync_WhenSubjectClaimMissing_ReturnsInvalidTokenClaims()
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
    public async Task RefreshTokenAsync_WhenSubjectClaimIsNotAnIdentifier_ReturnsInvalidTokenClaims()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(new Claim(AuthClaimTypes.Subject, "not-an-id")));

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Message == "Invalid access token claims.");
    }

    // The bearer handler maps `sub` onto NameIdentifier by default, so the same token reaches the
    // workflow under either name depending on which pipeline produced the principal.
    [Fact]
    public async Task RefreshTokenAsync_AcceptsTheMappedNameIdentifierFormOfTheSubjectClaim()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        var session = SeedSession(mocks, userId: 1, token: "stored-refresh");
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(new Claim(ClaimTypes.NameIdentifier, "1")));
        mocks.Repository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        session.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenUserMissing_ReturnsUnauthorizedByDefault()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(new Claim(AuthClaimTypes.Subject, "404")));
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
        var session = SeedSession(mocks, userId: 1, token: "stored-refresh");
        ArrangeRefreshFetch(mocks, user);
        sut.RefreshCandidateResult = Result.Failure(
            Error.Unauthorized("Auth.AccountDeactivated", "Account is deactivated."));

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.AccountDeactivated");
        session.IsRevoked.Should().BeFalse("a gate rejection is not a token problem");
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTokenValid_RotatesTheSession()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        var session = SeedSession(mocks, userId: 1, token: "stored-refresh");
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-1");
        result.Value.RefreshToken.Should().Be("refresh-1");

        session.IsRevoked.Should().BeTrue("BR-205 rotates the presented session out of use");
        session.RevokedAt.Should().Be(FixedNow.UtcDateTime);
        session.ReasonRevoked.Should().Be(RefreshSession.ReasonRotated);
        session.ReplacedByTokenHash.Should().Be(
            RefreshSession.HashToken("refresh-1"),
            "the rotation chain must be walkable from the retired session to its successor");

        var successor = mocks.Sessions.Saved.Should().ContainSingle(s => !s.IsRevoked).Subject;
        successor.TokenHash.Should().Be(RefreshSession.HashToken("refresh-1"));
        successor.ExpiresAt.Should().Be(FixedNow.UtcDateTime.AddDays(7));
    }

    [Fact]
    public async Task RefreshTokenAsync_RotationLeavesTheUsersOtherSessionsAlone()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        SeedSession(mocks, userId: 1, token: "stored-refresh");
        var otherDevice = SeedSession(mocks, userId: 1, token: "phone-token");
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        otherDevice.IsRevoked.Should().BeFalse();
    }

    // BR-206: a token that has already been rotated away comes back only if a copy outlived the
    // rotation, so the whole live family goes.
    [Fact]
    public async Task RefreshTokenAsync_WhenARotatedTokenIsReplayed_RevokesTheWholeFamily()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        SeedSession(mocks, userId: 1, token: "stored-refresh");
        var otherDevice = SeedSession(mocks, userId: 1, token: "phone-token");
        ArrangeRefreshFetch(mocks, user);

        // First use rotates it; the replay then arrives on the revoked row.
        await sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "stored-refresh"));
        Result<AuthenticationResponse> replay = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        replay.IsFailure.Should().BeTrue();
        replay.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        otherDevice.IsRevoked.Should().BeTrue("reuse detection cannot tell which device was stolen");
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
        mocks.Sessions.Saved.Should().OnlyContain(s => s.IsRevoked, "the successor minted by the first call goes too");
    }

    // H35: two requests presenting the same still-live token both read an un-revoked row. The store
    // arbitrates; the request that loses the claim must not walk away with a second live successor.
    [Fact]
    public async Task RefreshTokenAsync_WhenTheRotationLosesTheRace_FailsAndRevokesTheFamily()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        var presented = SeedSession(mocks, userId: 1, token: "stored-refresh");
        var otherDevice = SeedSession(mocks, userId: 1, token: "phone-token");
        var otherUsersSession = SeedSession(mocks, userId: 2, token: "someone-elses");
        ArrangeRefreshFetch(mocks, user);
        mocks.Sessions.RotationOutcome = () => false;

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        mocks.Sessions.Saved.Should().NotContain(
            s => string.Equals(s.TokenHash, RefreshSession.HashToken("refresh-1"), StringComparison.Ordinal),
            "the loser of the claim mints nothing");
        presented.IsRevoked.Should().BeTrue();
        otherDevice.IsRevoked.Should().BeTrue("losing the claim is indistinguishable from a replay (BR-206)");
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
        otherUsersSession.IsRevoked.Should().BeFalse("family revocation is scoped to the token's own user");
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTheRotationWinsTheClaim_StillRotates()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        var presented = SeedSession(mocks, userId: 1, token: "stored-refresh");
        ArrangeRefreshFetch(mocks, user);
        mocks.Sessions.RotationOutcome = () => true;

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsSuccess.Should().BeTrue();
        result.Value.RefreshToken.Should().Be("refresh-1");
        presented.IsRevoked.Should().BeTrue();
        presented.ReplacedByTokenHash.Should().Be(RefreshSession.HashToken("refresh-1"));
        mocks.Sessions.Saved.Should().ContainSingle(s => !s.IsRevoked);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenSessionExpired_FailsWithoutRevokingTheFamily()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        SeedSession(
            mocks,
            userId: 1,
            token: "stored-refresh",
            createdAt: FixedNow.UtcDateTime.AddDays(-8),
            expiresAt: FixedNow.UtcDateTime.AddSeconds(-1));
        var otherDevice = SeedSession(mocks, userId: 1, token: "phone-token");
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "stored-refresh"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        otherDevice.IsRevoked.Should().BeFalse(
            "an expired session is an ordinary end of life, not a theft signal");
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTokenUnknown_FailsWithoutRevokingTheFamily()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        var live = SeedSession(mocks, userId: 1, token: "stored-refresh");
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "never-issued"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        live.IsRevoked.Should().BeFalse(
            "an unknown hash proves nothing, and revoking on it would be a sign-out-everywhere lever");
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTheTokenBelongsToAnotherUser_Fails()
    {
        var (sut, mocks) = CreateSut();
        var user = CreateTestUser(id: 1);
        var otherUsersSession = SeedSession(mocks, userId: 2, token: "someone-elses");
        ArrangeRefreshFetch(mocks, user);

        Result<AuthenticationResponse> result = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("expired", "someone-elses"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        otherUsersSession.IsRevoked.Should().BeFalse();
    }

    // ── RevokeTokenAsync / RevokeAllSessionsAsync ──
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
        mocks.Sessions.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task RevokeTokenAsync_WithTheDevicesToken_RevokesOnlyThatSession()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository.Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(CreateTestUser(id: 5));
        var thisDevice = SeedSession(mocks, userId: 5, token: "this-device");
        var otherDevice = SeedSession(mocks, userId: 5, token: "other-device");

        Result result = await sut.RevokeTokenAsync(5, "this-device");

        result.IsSuccess.Should().BeTrue();
        thisDevice.IsRevoked.Should().BeTrue();
        thisDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonSignedOut);
        otherDevice.IsRevoked.Should().BeFalse("signing one device out is not signing out everywhere");
    }

    [Fact]
    public async Task RevokeTokenAsync_WithoutAToken_RevokesEverySession()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository.Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(CreateTestUser(id: 5));
        var first = SeedSession(mocks, userId: 5, token: "first");
        var second = SeedSession(mocks, userId: 5, token: "second");

        Result result = await sut.RevokeTokenAsync(5);

        result.IsSuccess.Should().BeTrue();
        first.IsRevoked.Should().BeTrue();
        second.IsRevoked.Should().BeTrue();
        mocks.Sessions.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_RevokesEveryLiveSessionOfThatUserOnly()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository.Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(CreateTestUser(id: 5));
        var mine = SeedSession(mocks, userId: 5, token: "mine");
        var someoneElse = SeedSession(mocks, userId: 6, token: "theirs");

        Result result = await sut.RevokeAllSessionsAsync(5);

        result.IsSuccess.Should().BeTrue();
        mine.IsRevoked.Should().BeTrue();
        mine.ReasonRevoked.Should().Be(RefreshSession.ReasonSignedOut);
        someoneElse.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_WhenUserMissing_ReturnsNotFound()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAuthUser?)null);

        Result result = await sut.RevokeAllSessionsAsync(404);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
    }

    // ── Helpers ──
    private static TestAuthUser CreateTestUser(UserIdentifierType id) => new() { Id = id };

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static void ArrangeLoginFetch(TestAuthenticationService sut, ServiceMocks mocks, TestAuthUser user)
    {
        sut.UntrackedUser = user;
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
    }

    private static void ArrangeRefreshFetch(ServiceMocks mocks, TestAuthUser user)
    {
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(new Claim(AuthClaimTypes.Subject, user.Id.ToString(CultureInfo.InvariantCulture))));
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
    }

    private static RefreshSession SeedSession(
        ServiceMocks mocks,
        UserIdentifierType userId,
        string token,
        DateTime? createdAt = null,
        DateTime? expiresAt = null)
    {
        var created = createdAt ?? FixedNow.UtcDateTime.AddHours(-1);
        var session = RefreshSession.Create(
            userId,
            token,
            created,
            expiresAt ?? created.AddDays(7)).Value!;
        mocks.Sessions.Seed(session);
        return session;
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
        Mock<ILoginProtectionService> LoginProtection,
        FakeRefreshSessionStore Sessions);

    private static (TestAuthenticationService Sut, ServiceMocks Mocks) CreateSut(
        bool loginRequestValid = true,
        bool registerRequestValid = true,
        bool refreshRequestValid = true,
        int maxActiveSessionsPerUser = 10)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestAuthUser, UserIdentifierType>>();
        var tokenService = new Mock<ITokenService>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var loginProtection = new Mock<ILoginProtectionService>();
        var sessions = new FakeRefreshSessionStore();

        unitOfWork
            .Setup(x => x.GetRepository<TestAuthUser, UserIdentifierType>())
            .Returns(repository.Object);
        unitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Distinct per call: rotation mints a successor while the predecessor is still in play, so a
        // constant would make the two sessions collide on the unique token hash.
        var issued = 0;
        tokenService
            .Setup(x => x.GenerateRefreshToken())
            .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));

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
            validators,
            sessions,
            Options.Create(new RefreshSessionSettings { MaxActiveSessionsPerUser = maxActiveSessionsPerUser }));

        var mocks = new ServiceMocks(unitOfWork, repository, tokenService, passwordHasher, loginProtection, sessions);
        return (sut, mocks);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

/// <summary>
/// In-memory <see cref="IRefreshSessionStore"/> mirroring the EF implementation's visibility rules:
/// a staged insert is invisible to queries until it is saved (EF does not read the change tracker
/// from a database query), and every read hands back the same live instance, so a revocation the
/// workflow performs is observable in the assertions.
/// </summary>
public sealed class FakeRefreshSessionStore : IRefreshSessionStore
{
    private readonly List<RefreshSession> _staged = [];
    private readonly List<RefreshSession> _saved = [];

    /// <summary>The persisted sessions.</summary>
    public IReadOnlyList<RefreshSession> Saved => _saved;

    /// <summary>How many times the workflow flushed.</summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// When set, decides whether <see cref="TryRotateAsync"/> claims the rotation. A false outcome
    /// stands in for the database arbitrating a concurrent rotation of the same token: the loser
    /// writes nothing at all.
    /// </summary>
    public Func<bool>? RotationOutcome { get; set; }

    /// <summary>Places an already-persisted session in the store.</summary>
    public void Seed(RefreshSession session) => _saved.Add(session);

    /// <inheritdoc />
    public Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default)
    {
        _staged.Add(session);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RefreshSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_saved.Find(s => string.Equals(s.TokenHash, tokenHash, StringComparison.Ordinal)));

    /// <inheritdoc />
    public Task<RefreshSession?> FindByIdAsync(
        Guid id,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_saved.Find(s => s.Id == id && s.UserId == userId));

    /// <inheritdoc />
    public Task<IReadOnlyList<RefreshSession>> GetUnrevokedByUserAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RefreshSession>>(
            [.. _saved
                .Where(s => s.UserId == userId && !s.IsRevoked)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id)]);

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        var written = _staged.Count;
        _saved.AddRange(_staged);
        _staged.Clear();
        return Task.FromResult(written);
    }

    /// <inheritdoc />
    public async Task<bool> TryRotateAsync(
        RefreshSession presented,
        RefreshSession successor,
        DateTime revokedAt,
        CancellationToken cancellationToken = default)
    {
        if (RotationOutcome is not null && !RotationOutcome())
        {
            return false;
        }

        if (presented.Revoke(revokedAt, RefreshSession.ReasonRotated, successor.TokenHash).IsFailure)
        {
            return false;
        }

        await AddAsync(successor, cancellationToken);
        await SaveChangesAsync(cancellationToken);

        return true;
    }
}

/// <summary>
/// Minimal <c>User</c> aggregate standing in for an app's Identity user. Public (not nested)
/// because Moq must proxy <c>IRepository</c> closed over this type.
/// </summary>
public sealed class TestAuthUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public byte[] PasswordHash { get; set; } = [1, 2, 3];

    public byte[] PasswordSalt { get; set; } = [4, 5, 6];
}

/// <summary>Concrete subclass supplying the per-app hooks the shared workflow calls.</summary>
public sealed class TestAuthenticationService(
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    TimeProvider timeProvider,
    AuthenticationValidators validators,
    IRefreshSessionStore refreshSessions,
    IOptions<RefreshSessionSettings> refreshSessionSettings)
    : AuthenticationServiceBase<TestAuthUser>(
        unitOfWork, tokenService, passwordHasher, loginProtection, timeProvider, validators, refreshSessions, refreshSessionSettings)
{
    public TestAuthUser? UntrackedUser { get; set; }

    public bool EmailExists { get; set; }

    /// <summary>Times the base asked. The post-save re-check that classifies a failed insert is the second.</summary>
    public int EmailExistsCallCount { get; private set; }

    public Result<TestAuthUser>? CreateUserResult { get; set; }

    public Result LoginCandidateResult { get; set; } = Result.Success();

    public Result RefreshCandidateResult { get; set; } = Result.Success();

    public Email? CapturedLoginEmail { get; private set; }

    protected override Task<TestAuthUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken)
    {
        CapturedLoginEmail = email;
        return Task.FromResult(UntrackedUser);
    }

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken)
    {
        EmailExistsCallCount++;
        return Task.FromResult(EmailExists);
    }

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
