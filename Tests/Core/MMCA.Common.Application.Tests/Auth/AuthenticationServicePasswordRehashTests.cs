using System.Globalization;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// The sign-in rehash: once a password has verified against a hash the hasher reports as outdated,
/// the shared workflow re-derives it and stores the result through
/// <see cref="IPasswordRehashableUser"/>, best effort, after the tokens are issued. The hasher is a
/// mock whose <see cref="IPasswordHasher.NeedsRehash"/> answers true for the 64-byte legacy shape and
/// false for the 69-byte current one, the same split the shipped hasher makes.
/// </summary>
public sealed class AuthenticationServicePasswordRehashTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] LegacySalt = [.. Enumerable.Repeat((byte)4, 32)];
    private static readonly byte[] CurrentHash = [0x01, .. Enumerable.Repeat((byte)7, 68)];
    private static readonly byte[] CurrentSalt = [.. Enumerable.Repeat((byte)8, 32)];

    private static byte[] LegacyHash() => [.. Enumerable.Repeat((byte)3, 64)];

    [Fact]
    public async Task LoginAsync_WithALegacyHashOnARehashableUser_StoresTheRehashAndSavesIt()
    {
        var user = new RehashableAuthUser { Id = 1, PasswordHash = LegacyHash(), PasswordSalt = LegacySalt };
        var harness = new Harness<RehashableAuthUser>(user);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        user.RehashCount.Should().Be(1);
        user.PasswordHash.Should().Equal(CurrentHash);
        user.PasswordSalt.Should().Equal(CurrentSalt);
        harness.PasswordHasher.Verify(x => x.HashPassword("pw"), Times.Once);
        harness.UnitOfWork.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "the session save goes through the refresh-session store, so the rehash needs its own save to persist");
    }

    [Fact]
    public async Task LoginAsync_ASecondTimeAfterTheRehash_DoesNotRehashAgain()
    {
        var user = new RehashableAuthUser { Id = 1, PasswordHash = LegacyHash(), PasswordSalt = LegacySalt };
        var harness = new Harness<RehashableAuthUser>(user);

        (await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"))).IsSuccess.Should().BeTrue();
        Result<AuthenticationResponse> second = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        second.IsSuccess.Should().BeTrue();
        user.RehashCount.Should().Be(1, "the stored hash is current after the first sign-in");
        harness.PasswordHasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Once);
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WithAUserThatDoesNotOptIn_SignsInAndLeavesTheHashUntouched()
    {
        var legacy = LegacyHash();
        var user = new TestAuthUser { Id = 1, PasswordHash = legacy, PasswordSalt = LegacySalt };
        var harness = new Harness<TestAuthUser>(user);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().BeSameAs(legacy);
        harness.PasswordHasher.Verify(x => x.NeedsRehash(It.IsAny<byte[]>()), Times.Never);
        harness.PasswordHasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Never);
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenTheRehashSaveThrows_StillSignsInAndLogsTheFailure()
    {
        var user = new RehashableAuthUser { Id = 1, PasswordHash = LegacyHash(), PasswordSalt = LegacySalt };
        var harness = new Harness<RehashableAuthUser>(user);
        harness.UnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue("a rehash is an upgrade, never a precondition of signing in");
        result.Value!.AccessToken.Should().NotBeNullOrEmpty();
        harness.Logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenTheHasherThrowsDuringTheRehash_StillSignsIn()
    {
        var user = new RehashableAuthUser { Id = 1, PasswordHash = LegacyHash(), PasswordSalt = LegacySalt };
        var harness = new Harness<RehashableAuthUser>(user);
        harness.PasswordHasher
            .Setup(x => x.HashPassword(It.IsAny<string>()))
            .Throws(new System.Security.Cryptography.CryptographicException("derivation failed"));

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        user.RehashCount.Should().Be(0);
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenTheAggregateRefusesTheRehash_StillSignsInWithoutSaving()
    {
        var legacy = LegacyHash();
        var user = new RehashableAuthUser
        {
            Id = 1,
            PasswordHash = legacy,
            PasswordSalt = LegacySalt,
            RehashResult = Result.Failure(Error.Validation("User.PasswordHash.Invalid", "refused")),
        };
        var harness = new Harness<RehashableAuthUser>(user);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "pw"));

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().BeSameAs(legacy);
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WithAWrongPassword_NeverRehashes()
    {
        var user = new RehashableAuthUser { Id = 1, PasswordHash = LegacyHash(), PasswordSalt = LegacySalt };
        var harness = new Harness<RehashableAuthUser>(user);
        harness.PasswordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(false);

        Result<AuthenticationResponse> result = await harness.Sut.LoginAsync(new LoginRequest("user@example.com", "wrong"));

        result.IsFailure.Should().BeTrue();
        user.RehashCount.Should().Be(0);
        harness.PasswordHasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Never);
    }

    private sealed class Harness<TUser>
        where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
    {
        public Harness(TUser user)
        {
            var repository = new Mock<IRepository<TUser, UserIdentifierType>>();
            repository.Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
            UnitOfWork.Setup(x => x.GetRepository<TUser, UserIdentifierType>()).Returns(repository.Object);
            UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var issued = 0;
            TokenService.Setup(x => x.GenerateRefreshToken())
                .Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++issued}"));

            PasswordHasher
                .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
                .Returns(true);
            PasswordHasher.Setup(x => x.NeedsRehash(It.IsAny<byte[]>())).Returns((byte[] hash) => hash.Length == 64);
            PasswordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns((CurrentHash, CurrentSalt));

            LoginProtection
                .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            Logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var validators = new AuthenticationValidators(
                AlwaysValid<LoginRequest>(),
                AlwaysValid<RegisterRequest>(),
                AlwaysValid<RefreshTokenRequest>());

            Sut = new RehashTestAuthenticationService<TUser>(
                UnitOfWork.Object,
                PasswordHasher.Object,
                LoginProtection.Object,
                validators,
                new AuthSessionIssuer(
                    TokenService.Object,
                    new InMemoryRefreshSessionStore(),
                    Options.Create(new RefreshSessionSettings()),
                    new FixedClock(FixedNow)),
                Logger.Object)
            {
                UntrackedUser = user,
            };
        }

        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public Mock<IPasswordHasher> PasswordHasher { get; } = new();

        public Mock<ILoginProtectionService> LoginProtection { get; } = new();

        public Mock<ITokenService> TokenService { get; } = new();

        public Mock<ILogger> Logger { get; } = new();

        public RehashTestAuthenticationService<TUser> Sut { get; }

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

/// <summary>A user aggregate that opts in to the sign-in rehash. Public because Moq proxies <c>IRepository</c> over it.</summary>
public sealed class RehashableAuthUser : AuditableAggregateRootEntity<UserIdentifierType>, IPasswordRehashableUser
{
    public byte[] PasswordHash { get; set; } = [];

    public byte[] PasswordSalt { get; set; } = [];

    public int RehashCount { get; private set; }

    public Result RehashResult { get; set; } = Result.Success();

    public Result RehashPassword(byte[] newPasswordHash, byte[] newPasswordSalt)
    {
        if (RehashResult.IsFailure)
        {
            return RehashResult;
        }

        PasswordHash = newPasswordHash;
        PasswordSalt = newPasswordSalt;
        RehashCount++;
        return Result.Success();
    }
}

/// <summary>Concrete subclass over any test user, overriding the logger the way an app would.</summary>
/// <typeparam name="TUser">The test user aggregate.</typeparam>
public sealed class RehashTestAuthenticationService<TUser>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    AuthenticationValidators validators,
    IAuthSessionIssuer sessionIssuer,
    ILogger logger)
    : AuthenticationServiceBase<TUser>(
        unitOfWork,
        passwordHasher,
        loginProtection,
        validators,
        sessionIssuer)
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    public TUser? UntrackedUser { get; set; }

    protected override ILogger Logger => logger;

    protected override Task<TUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(UntrackedUser);

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    protected override Result<TUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt) =>
        throw new NotSupportedException("Registration is not exercised by the rehash tests.");

    protected override string CreateAccessToken(TUser user) =>
        string.Create(CultureInfo.InvariantCulture, $"access-{user.Id}");
}
