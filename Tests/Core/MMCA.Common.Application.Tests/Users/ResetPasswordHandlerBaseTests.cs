using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users.UseCases.ResetPassword;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared reset-password workflow: every rejection collapses to the one generic
/// <c>Auth.InvalidResetToken</c> error, the write happens only when the aggregate accepts it, and a
/// successful reset clears the account's lockout so the new credential is immediately usable.
/// </summary>
public sealed class ResetPasswordHandlerBaseTests
{
    private const string Email = "user@example.com";
    private const string Token = "reset-token";
    private const string NewPassword = "New-Password1!";

    private static readonly byte[] NewHash = [7, 7, 7];
    private static readonly byte[] NewSalt = [8, 8, 8];

    [Fact]
    public async Task HandleAsync_WhenTokenRejected_ReturnsGenericErrorWithoutTouchingTheRepository()
    {
        var (sut, mocks) = CreateSut();
        mocks.TokenService
            .Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<UserIdentifierType>(
                Error.Unauthorized("Auth.InvalidResetToken", "invalid")));

        Result result = await sut.HandleAsync(Command());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidResetToken"
            && e.Type == ErrorType.Unauthorized
            && e.Source == nameof(TestResetPasswordHandler));
        mocks.UnitOfWork.Verify(x => x.GetRepository<TestIdentityUser, UserIdentifierType>(), Times.Never);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenAccountNoLongerResolvable_ReturnsTheSameGenericError()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestIdentityUser?)null);

        Result result = await sut.HandleAsync(Command());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidResetToken",
            "a vanished account must not be distinguishable from a bad token");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenTokenValid_WritesNewMaterialSavesOnceAndClearsLockout()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser { Id = 42 };
        mocks.Repository
            .Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Result result = await sut.HandleAsync(Command());

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().BeEquivalentTo(NewHash);
        user.PasswordSalt.Should().BeEquivalentTo(NewSalt);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        mocks.LoginProtection.Verify(
            x => x.ResetFailedAttemptsAsync(Email, It.IsAny<CancellationToken>()),
            Times.Once,
            "a user who reset because of a lockout must not stay locked out");
    }

    [Fact]
    public async Task HandleAsync_WhenAggregateRejectsChange_ReturnsFailureWithoutSavingOrClearingLockout()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser
        {
            Id = 42,
            ForcedFailure = Error.Invariant("User.PasswordReused", "New password must differ."),
        };
        mocks.Repository
            .Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Result result = await sut.HandleAsync(Command());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "User.PasswordReused");
        mocks.UnitOfWork.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "an invariant failure must not persist");
        mocks.LoginProtection.Verify(
            x => x.ResetFailedAttemptsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ConsumesTheTokenBeforeSaving()
    {
        // Deliberate ordering: leaving the token live until the write succeeds would open a replay
        // window. A token burned by a later failure costs the user one more reset request.
        var (sut, mocks) = CreateSut();
        var order = new List<string>();
        mocks.TokenService
            .Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<UserIdentifierType>(42))
            .Callback(() => order.Add("consume"));
        mocks.Repository
            .Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestIdentityUser { Id = 42 });
        mocks.UnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .Callback(() => order.Add("save"));

        await sut.HandleAsync(Command());

        order.Should().Equal("consume", "save");
    }

    [Fact]
    public async Task HandleAsync_WhenTheResetSucceeds_RevokesEveryLiveRefreshSession()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestIdentityUser { Id = 42 });
        var live = RefreshSession.Create(
            42,
            "stolen-chain",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc)).Value!;
        mocks.RefreshSessions
            .Setup(x => x.GetUnrevokedByUserAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync([live]);

        Result result = await sut.HandleAsync(Command());

        result.IsSuccess.Should().BeTrue();
        live.RevokedAt.Should().NotBeNull(
            "a reset is the remediation for a suspected intruder, so the intruder's chain dies with the old password");
        mocks.RefreshSessions.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TestResetPasswordCommand Command() =>
        new(new ResetPasswordRequest(Email, Token, NewPassword));

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestIdentityUser, UserIdentifierType>> Repository,
        Mock<IPasswordHasher> PasswordHasher,
        Mock<IPasswordResetTokenService> TokenService,
        Mock<ILoginProtectionService> LoginProtection,
        Mock<IRefreshSessionStore> RefreshSessions);

    private static (TestResetPasswordHandler Sut, HandlerMocks Mocks) CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestIdentityUser, UserIdentifierType>>();
        var passwordHasher = new Mock<IPasswordHasher>();
        var tokenService = new Mock<IPasswordResetTokenService>();
        var loginProtection = new Mock<ILoginProtectionService>();

        unitOfWork.Setup(x => x.GetRepository<TestIdentityUser, UserIdentifierType>()).Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        passwordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns((NewHash, NewSalt));

        tokenService
            .Setup(x => x.ValidateAndConsumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<UserIdentifierType>(42));

        var refreshSessions = new Mock<IRefreshSessionStore>();
        refreshSessions
            .Setup(x => x.GetUnrevokedByUserAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = new TestResetPasswordHandler(
            unitOfWork.Object,
            passwordHasher.Object,
            tokenService.Object,
            loginProtection.Object,
            refreshSessions.Object);

        return (sut, new HandlerMocks(
            unitOfWork, repository, passwordHasher, tokenService, loginProtection, refreshSessions));
    }
}

/// <summary>App-side reset-password command shape (the shared request payload only).</summary>
public sealed record TestResetPasswordCommand(ResetPasswordRequest Request)
    : ICommandWithRequest<ResetPasswordRequest>;

/// <summary>Concrete subclass standing in for an app's <c>ResetPasswordHandler</c>.</summary>
public sealed class TestResetPasswordHandler(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IPasswordResetTokenService tokenService,
    ILoginProtectionService loginProtection,
    IRefreshSessionStore? refreshSessions = null)
    : ResetPasswordHandlerBase<TestIdentityUser, TestResetPasswordCommand>(
        unitOfWork, passwordHasher, tokenService, loginProtection, NullLogger.Instance, refreshSessions);
