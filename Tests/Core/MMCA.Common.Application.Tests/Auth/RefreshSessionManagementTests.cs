using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.ValueObjects;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Covers the per-device session surface layered on the BR-205/206 workflow: the <c>sid</c> claim
/// stamped on every issued access token, the device list, and per-device revocation.
/// <para>
/// The subclass here mints through the base's <c>TokenService</c> property (the way both shipped
/// consumers do), which is the extension point that puts <c>sid</c> on the token without the app's
/// hook knowing the claim exists.
/// </para>
/// </summary>
public sealed class RefreshSessionManagementTests
{
    private const UserIdentifierType UserId = 42;
    private const UserIdentifierType OtherUserId = 43;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = FixedNow.UtcDateTime;

    // ── sid claim: issuance ──
    [Fact]
    public async Task LoginAsync_StampsTheNewSessionsIdOnTheAccessToken()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(sut, mocks, UserId);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        mocks.Sessions.Saved.Should().ContainSingle();
        var expected = mocks.Sessions.Saved[0].Id;
        SessionIdOf(mocks, result.Value!.AccessToken).Should().Be(
            expected,
            "the token names the device it was minted for, and that is the session just opened");
    }

    [Fact]
    public async Task LoginAsync_KeepsTheAppsOwnClaimsAlongsideSid()
    {
        var (sut, mocks) = CreateSut();
        sut.AdditionalClaims = [new Claim("speaker_id", "7")];
        ArrangeUser(sut, mocks, UserId);

        Result<AuthenticationResponse> result = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        List<Claim> claims = mocks.MintedClaims[result.Value!.AccessToken];
        claims.Should().Contain(c => c.Type == "speaker_id" && c.Value == "7");
        claims.Should().ContainSingle(c => c.Type == AuthClaimTypes.SessionId);
    }

    [Fact]
    public async Task RegisterAsync_StampsTheNewSessionsIdOnTheAccessToken()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestAuthUser?)null);

        Result<AuthenticationResponse> result = await sut.RegisterAsync(
            new RegisterRequest("new@example.com", "Password123!", "John", "Doe"));

        result.IsSuccess.Should().BeTrue();
        SessionIdOf(mocks, result.Value!.AccessToken).Should().Be(mocks.Sessions.Saved[0].Id);
    }

    // ── sid claim: rotation ──
    [Fact]
    public async Task RefreshTokenAsync_StampsTheSuccessorSessionsIdNotThePredecessors()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(sut, mocks, UserId);

        Result<AuthenticationResponse> login = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));
        var originalSessionId = mocks.Sessions.Saved[0].Id;
        ArrangeExpiredPrincipal(mocks, UserId);

        Result<AuthenticationResponse> refreshed = await sut.RefreshTokenAsync(
            new RefreshTokenRequest(login.Value!.AccessToken, login.Value.RefreshToken));

        refreshed.IsSuccess.Should().BeTrue();
        RefreshSession successor = mocks.Sessions.Saved.Single(s => !s.IsRevoked);
        successor.Id.Should().NotBe(originalSessionId, "rotation opens a new row with a new id");
        SessionIdOf(mocks, refreshed.Value!.AccessToken).Should().Be(
            successor.Id,
            "the current-device marker must follow the rotation, not point at the session it revoked");
    }

    // ── sid claim: additive ──
    [Fact]
    public async Task RefreshTokenAsync_AcceptsAnAccessTokenThatCarriesNoSidClaim()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(sut, mocks, UserId);
        Result<AuthenticationResponse> login = await sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        // A token minted before `sid` shipped: `sub` only, no session claim at all.
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthClaimTypes.Subject, UserId.ToString(CultureInfo.InvariantCulture))],
                authenticationType: "Test")));

        Result<AuthenticationResponse> refreshed = await sut.RefreshTokenAsync(
            new RefreshTokenRequest("legacy-token", login.Value!.RefreshToken));

        refreshed.IsSuccess.Should().BeTrue(
            "sid is additive: nothing validates against it, so a token that predates it still refreshes");
        SessionIdOf(mocks, refreshed.Value!.AccessToken).Should().NotBeNull(
            "the replacement token does carry the claim");
    }

    [Fact]
    public void FindSessionId_OnAPrincipalWithoutTheClaim_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthClaimTypes.Subject, "1")],
            authenticationType: "Test"));

        principal.FindSessionId().Should().BeNull();
    }

    // ── GetSessionsAsync ──
    [Fact]
    public async Task GetSessionsAsync_ReturnsOnlyLiveSessions_NewestFirst()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession oldest = SeedSession(mocks, UserId, createdAt: Now.AddDays(-3), token: "a");
        RefreshSession newest = SeedSession(mocks, UserId, createdAt: Now.AddDays(-1), token: "b");
        RefreshSession revoked = SeedSession(mocks, UserId, createdAt: Now.AddHours(-2), token: "c");
        revoked.Revoke(Now, RefreshSession.ReasonSignedOut);
        SeedSession(mocks, UserId, createdAt: Now.AddDays(-30), token: "d", expiresAt: Now.AddDays(-1));
        SeedSession(mocks, OtherUserId, createdAt: Now, token: "e");

        Result<IReadOnlyList<RefreshSessionSummaryResponse>> result = await sut.GetSessionsAsync(UserId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(s => s.SessionId).Should().Equal(
            [newest.Id, oldest.Id],
            "revoked, expired and other users' sessions are all out, and the live ones come back newest first");
    }

    [Fact]
    public async Task GetSessionsAsync_MarksOnlyTheCallersOwnSessionAsCurrent()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession mine = SeedSession(mocks, UserId, createdAt: Now.AddDays(-1), token: "a");
        RefreshSession other = SeedSession(mocks, UserId, createdAt: Now.AddDays(-2), token: "b");

        Result<IReadOnlyList<RefreshSessionSummaryResponse>> result = await sut.GetSessionsAsync(UserId, mine.Id);

        IReadOnlyList<RefreshSessionSummaryResponse> summaries = result.Value!;
        summaries.Single(s => s.SessionId == mine.Id).IsCurrent.Should().BeTrue();
        summaries.Single(s => s.SessionId == other.Id).IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task GetSessionsAsync_WithNoCurrentSessionId_MarksNothingCurrent()
    {
        var (sut, mocks) = CreateSut();
        SeedSession(mocks, UserId, createdAt: Now.AddDays(-1), token: "a");

        Result<IReadOnlyList<RefreshSessionSummaryResponse>> result = await sut.GetSessionsAsync(UserId);

        result.Value!.Should().OnlyContain(s => !s.IsCurrent,
            "a caller whose token predates the sid claim has no identifiable device");
    }

    [Fact]
    public async Task GetSessionsAsync_CarriesTheDeviceFieldsAndNeverTheTokenMaterial()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession session = SeedSession(
            mocks, UserId, createdAt: Now.AddDays(-1), token: "a", ipAddress: "203.0.113.7", userAgent: "Firefox");

        Result<IReadOnlyList<RefreshSessionSummaryResponse>> result = await sut.GetSessionsAsync(UserId);

        RefreshSessionSummaryResponse summary = result.Value!.Single();
        summary.IpAddress.Should().Be("203.0.113.7");
        summary.UserAgent.Should().Be("Firefox");
        summary.CreatedAt.Should().Be(session.CreatedAt);
        summary.ExpiresAt.Should().Be(session.ExpiresAt);
        typeof(RefreshSessionSummaryResponse).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(["TokenHash", "ReplacedByTokenHash"],
                "the response must never carry the credential digest or the rotation chain");
    }

    [Fact]
    public async Task GetSessionsAsync_ForAUserWithNoSessions_ReturnsAnEmptyList()
    {
        var (sut, _) = CreateSut();

        Result<IReadOnlyList<RefreshSessionSummaryResponse>> result = await sut.GetSessionsAsync(UserId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }

    // ── RevokeSessionByIdAsync ──
    [Fact]
    public async Task RevokeSessionByIdAsync_OwnedLiveSession_RevokesItAsSignedOutAndSaves()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession session = SeedSession(mocks, UserId, createdAt: Now.AddDays(-1), token: "a");
        RefreshSession untouched = SeedSession(mocks, UserId, createdAt: Now.AddDays(-2), token: "b");

        Result result = await sut.RevokeSessionByIdAsync(UserId, session.Id);

        result.IsSuccess.Should().BeTrue();
        session.IsRevoked.Should().BeTrue();
        session.RevokedAt.Should().Be(Now);
        session.ReasonRevoked.Should().Be(RefreshSession.ReasonSignedOut);
        untouched.IsRevoked.Should().BeFalse("signing one device out must leave the others alone");
        mocks.Sessions.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeSessionByIdAsync_UnknownSession_ReturnsNotFound()
    {
        var (sut, _) = CreateSut();

        Result result = await sut.RevokeSessionByIdAsync(UserId, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.SessionNotFound" && e.Type == ErrorType.NotFound);
    }

    [Fact]
    public async Task RevokeSessionByIdAsync_AnotherUsersSession_ReturnsNotFoundAndLeavesItLive()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession theirs = SeedSession(mocks, OtherUserId, createdAt: Now.AddDays(-1), token: "a");

        Result result = await sut.RevokeSessionByIdAsync(UserId, theirs.Id);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.SessionNotFound" && e.Type == ErrorType.NotFound,
            "another account's id must be indistinguishable from one that never existed");
        theirs.IsRevoked.Should().BeFalse();
        mocks.Sessions.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task RevokeSessionByIdAsync_AlreadyRevokedSession_SucceedsWithoutWriting()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession session = SeedSession(mocks, UserId, createdAt: Now.AddDays(-1), token: "a");
        session.Revoke(Now.AddHours(-1), RefreshSession.ReasonSignedOut);

        Result result = await sut.RevokeSessionByIdAsync(UserId, session.Id);

        result.IsSuccess.Should().BeTrue(
            "the caller asked for that device to be signed out and it is; a duplicate click is not an error");
        session.RevokedAt.Should().Be(Now.AddHours(-1), "the first revocation's instant is the one kept");
        mocks.Sessions.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task RevokeSessionByIdAsync_ExpiredButUnrevokedSession_StillRevokesIt()
    {
        var (sut, mocks) = CreateSut();
        RefreshSession expired = SeedSession(
            mocks, UserId, createdAt: Now.AddDays(-30), token: "a", expiresAt: Now.AddDays(-1));

        Result result = await sut.RevokeSessionByIdAsync(UserId, expired.Id);

        result.IsSuccess.Should().BeTrue();
        expired.IsRevoked.Should().BeTrue(
            "an expired row is still the user's row to close, and closing it takes it out of any list");
    }

    // ── Harness ──
    private sealed record ServiceMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestAuthUser, UserIdentifierType>> Repository,
        Mock<ITokenService> TokenService,
        FakeRefreshSessionStore Sessions,
        Dictionary<string, List<Claim>> MintedClaims);

    private static (SessionAwareAuthenticationService Sut, ServiceMocks Mocks) CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestAuthUser, UserIdentifierType>>();
        var tokenService = new Mock<ITokenService>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var loginProtection = new Mock<ILoginProtectionService>();
        var sessions = new FakeRefreshSessionStore();
        var mintedClaims = new Dictionary<string, List<Claim>>(StringComparer.Ordinal);

        unitOfWork.Setup(x => x.GetRepository<TestAuthUser, UserIdentifierType>()).Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var issued = 0;
        tokenService
            .Setup(x => x.GenerateRefreshToken())
            .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));

        // Each minted token gets a unique name and records the claim set it was minted from, so a
        // test can read the `sid` back without decoding a real JWT.
        var minted = 0;
        tokenService
            .Setup(x => x.GenerateAccessToken(
                It.IsAny<UserIdentifierType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>?>()))
            .Returns((UserIdentifierType _, string _, string _, string _, IEnumerable<Claim>? claims) =>
            {
                var token = string.Create(CultureInfo.InvariantCulture, $"access-{++minted}");
                mintedClaims[token] = claims is null ? [] : [.. claims];
                return token;
            });

        passwordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        passwordHasher
            .Setup(x => x.HashPassword(It.IsAny<string>()))
            .Returns(([9], [8]));

        loginProtection
            .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        loginProtection
            .Setup(x => x.CheckRegistrationRateLimitAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var validators = new AuthenticationValidators(
            AlwaysValid<LoginRequest>(),
            AlwaysValid<RegisterRequest>(),
            AlwaysValid<RefreshTokenRequest>());

        var sut = new SessionAwareAuthenticationService(
            unitOfWork.Object,
            tokenService.Object,
            passwordHasher.Object,
            loginProtection.Object,
            new FixedTimeProvider(FixedNow),
            validators,
            sessions,
            Options.Create(new RefreshSessionSettings()));

        return (sut, new ServiceMocks(unitOfWork, repository, tokenService, sessions, mintedClaims));
    }

    private static IValidator<T> AlwaysValid<T>()
    {
        var validator = new Mock<IValidator<T>>();
        validator
            .Setup(x => x.ValidateAsync(It.IsAny<T>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator.Object;
    }

    private static void ArrangeUser(SessionAwareAuthenticationService sut, ServiceMocks mocks, UserIdentifierType id)
    {
        var user = new TestAuthUser { Id = id };
        sut.UntrackedUser = user;
        mocks.Repository
            .Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
    }

    private static void ArrangeExpiredPrincipal(ServiceMocks mocks, UserIdentifierType id) =>
        mocks.TokenService
            .Setup(x => x.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthClaimTypes.Subject, id.ToString(CultureInfo.InvariantCulture))],
                authenticationType: "Test")));

    /// <summary>The <c>sid</c> the given token was minted with, read back off the recorded claim set.</summary>
    private static Guid? SessionIdOf(ServiceMocks mocks, string accessToken)
    {
        var value = mocks.MintedClaims[accessToken]
            .Find(c => string.Equals(c.Type, AuthClaimTypes.SessionId, StringComparison.Ordinal))?.Value;
        return Guid.TryParse(value, CultureInfo.InvariantCulture, out var sessionId) ? sessionId : null;
    }

    private static RefreshSession SeedSession(
        ServiceMocks mocks,
        UserIdentifierType userId,
        DateTime createdAt,
        string token,
        DateTime? expiresAt = null,
        string? ipAddress = null,
        string? userAgent = null)
    {
        RefreshSession session = RefreshSession.Create(
            userId,
            token,
            createdAt,
            expiresAt ?? createdAt.AddDays(7),
            ipAddress,
            userAgent).Value!;
        mocks.Sessions.Seed(session);
        return session;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

/// <summary>
/// Concrete subclass that mints through the base's <see cref="ITokenService"/> property, the way both
/// shipped consumers do, so the <c>sid</c>-stamping wrapper is actually exercised.
/// </summary>
public sealed class SessionAwareAuthenticationService(
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
    /// <summary>The user the login lookup returns.</summary>
    public TestAuthUser? UntrackedUser { get; set; }

    /// <summary>App-specific claims the token hook passes through, standing in for e.g. speaker_id.</summary>
    public IReadOnlyList<Claim>? AdditionalClaims { get; set; }

    /// <inheritdoc />
    protected override Task<TestAuthUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(UntrackedUser);

    /// <inheritdoc />
    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <inheritdoc />
    protected override Result<TestAuthUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        Result.Success(new TestAuthUser { Id = 77, PasswordHash = passwordHash, PasswordSalt = passwordSalt });

    /// <inheritdoc />
    protected override string CreateAccessToken(TestAuthUser user) =>
        TokenService.GenerateAccessToken(user.Id, "user@example.com", "Attendee", "Test User", AdditionalClaims);
}
