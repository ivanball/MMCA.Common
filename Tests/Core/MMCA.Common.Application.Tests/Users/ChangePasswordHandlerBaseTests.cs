using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Users.UseCases.ChangePassword;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared password-change workflow (ADR-032): verify-before-write ordering, the
/// no-save-on-invariant-failure rule, and the error payload the app handlers reported before the hoist.
/// </summary>
public sealed class ChangePasswordHandlerBaseTests
{
    private static readonly byte[] NewHash = [7, 7, 7];
    private static readonly byte[] NewSalt = [8, 8, 8];

    [Fact]
    public async Task HandleAsync_WhenUserMissing_ReturnsNotFoundWithHandlerSourceAndEntityTarget()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestIdentityUser?)null);

        Result result = await sut.HandleAsync(new TestChangePasswordCommand(404, new ChangePasswordRequest("old", "new")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Type == ErrorType.NotFound
            && e.Source == nameof(TestChangePasswordHandler)
            && e.Target == nameof(TestIdentityUser));
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenCurrentPasswordWrong_ReturnsUnauthorizedWithoutHashingOrSaving()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });
        mocks.PasswordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(false);

        Result result = await sut.HandleAsync(new TestChangePasswordCommand(1, new ChangePasswordRequest("wrong", "new")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "Auth.InvalidCurrentPassword" && e.Type == ErrorType.Unauthorized);
        mocks.PasswordHasher.Verify(x => x.HashPassword(It.IsAny<string>()), Times.Never);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenCurrentPasswordCorrect_WritesNewMaterialAndSaves()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser { Id = 1 };
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestChangePasswordCommand(1, new ChangePasswordRequest("old", "new")));

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().BeEquivalentTo(NewHash);
        user.PasswordSalt.Should().BeEquivalentTo(NewSalt);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenAggregateRejectsChange_ReturnsFailureWithoutSaving()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestIdentityUser
        {
            Id = 1,
            ForcedFailure = Error.Invariant("User.PasswordReused", "New password must differ."),
        };
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestChangePasswordCommand(1, new ChangePasswordRequest("old", "new")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "User.PasswordReused");
        mocks.UnitOfWork.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "an invariant failure must not persist");
    }

    private static void ArrangeUser(HandlerMocks mocks, TestIdentityUser user) =>
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestIdentityUser, UserIdentifierType>> Repository,
        Mock<IPasswordHasher> PasswordHasher);

    private static (TestChangePasswordHandler Sut, HandlerMocks Mocks) CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestIdentityUser, UserIdentifierType>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        unitOfWork.Setup(x => x.GetRepository<TestIdentityUser, UserIdentifierType>()).Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        passwordHasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        passwordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns((NewHash, NewSalt));

        var sut = new TestChangePasswordHandler(unitOfWork.Object, passwordHasher.Object);
        return (sut, new HandlerMocks(unitOfWork, repository, passwordHasher));
    }
}

/// <summary>Concrete subclass standing in for an app's <c>ChangePasswordHandler</c>.</summary>
public sealed class TestChangePasswordHandler(IUnitOfWork unitOfWork, IPasswordHasher passwordHasher)
    : ChangePasswordHandlerBase<TestIdentityUser, TestChangePasswordCommand>(
        unitOfWork, passwordHasher, NullLogger.Instance);
