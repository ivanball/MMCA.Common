using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Users.UseCases.DeleteUser;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared account-erasure workflow: the owner-or-privileged-role gate, the
/// delete-then-anonymize-then-save ordering, the app tail hook, and the post-commit queue.
/// </summary>
public sealed class DeleteUserHandlerBaseTests
{
    private const string PrivilegedRole = "Organizer";

    [Fact]
    public async Task HandleAsync_WhenCallerIsNeitherOwnerNorPrivileged_ReturnsForbiddenWithoutFetching()
    {
        var (sut, mocks) = CreateSut();

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 2, "Attendee"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "User.DeleteForbidden"
            && e.Type == ErrorType.Forbidden
            && e.Source == nameof(TestDeleteUserHandler)
            && e.Target == "UserId");
        mocks.Repository.Verify(
            x => x.GetByIdAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerHoldsPrivilegedRole_ErasesAnotherAccount()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 2, PrivilegedRole));

        result.IsSuccess.Should().BeTrue();
        user.IsDeleted.Should().BeTrue();
        user.AnonymizeCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenUserMissing_ReturnsNotFoundWithoutSaving()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestHidingDeleteUser?)null);

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 404, CurrentUserId: 404, null));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Type == ErrorType.NotFound && e.Target == nameof(TestHidingDeleteUser));
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_CallsTheAggregatesOwnDeleteEvenWhenItHidesTheBaseMethod()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        user.UpdateRefreshToken("outstanding", DateTime.UtcNow.AddDays(1));
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 1, null));

        result.IsSuccess.Should().BeTrue();
        user.HidingDeleteCalled.Should().BeTrue(
            "IErasableUser re-maps the interface onto the aggregate's own hiding Delete()");
        user.RevokeCalled.Should().BeTrue("the app couples refresh-token revocation to deletion");
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyDeleted_ReturnsAlreadyDeletedBeforeRunningTheAppTail()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        user.Delete();
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 1, null));

        result.IsFailure.Should().BeTrue();
        sut.TailInvocations.Should().Be(0, "an already-deleted account fails on its own error, not a cascaded one");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenAppTailFails_AbortsBeforeAnonymizingOrSaving()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        ArrangeUser(mocks, user);
        sut.TailResult = Result.Failure(Error.Invariant("Customer.AlreadyDeleted", "Linked profile already deleted."));

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 1, null));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Customer.AlreadyDeleted");
        user.AnonymizeCalled.Should().BeFalse();
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_RunsTheAppTailBeforeAnonymizeSoItStillSeesPersonalData()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        ArrangeUser(mocks, user);

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 1, null));

        result.IsSuccess.Should().BeTrue();
        sut.TailSawAnonymizedUser.Should().BeFalse();
        sut.TailSawDeletedUser.Should().BeTrue("the tail runs after the soft-delete and before the erasure");
    }

    [Fact]
    public async Task HandleAsync_WhenAnonymizeFails_DoesNotSave()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        ArrangeUser(mocks, user);
        sut.OnTail = () => user.ForcedFailure = Error.Invariant("User.Anonymize.Invalid", "Placeholder email invalid.");

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 1, null));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "User.Anonymize.Invalid");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_RunsPostCommitActionsInOrderOnlyAfterTheSave()
    {
        var (sut, mocks) = CreateSut();
        var user = new TestHidingDeleteUser { Id = 1 };
        ArrangeUser(mocks, user);

        var trace = new List<string>();
        mocks.UnitOfWork
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .Callback(() => trace.Add("save"));
        sut.PostCommitActions =
        [
            _ => TraceAsync(trace, "marker"),
            _ => TraceAsync(trace, "blob"),
        ];

        Result result = await sut.HandleAsync(new TestDeleteUserCommand(UserId: 1, CurrentUserId: 1, null));

        result.IsSuccess.Should().BeTrue();
        trace.Should().Equal("save", "marker", "blob");
    }

    private static Task TraceAsync(List<string> trace, string entry)
    {
        trace.Add(entry);
        return Task.CompletedTask;
    }

    private static void ArrangeUser(HandlerMocks mocks, TestHidingDeleteUser user) =>
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestHidingDeleteUser, UserIdentifierType>> Repository);

    private static (TestDeleteUserHandler Sut, HandlerMocks Mocks) CreateSut()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestHidingDeleteUser, UserIdentifierType>>();

        unitOfWork.Setup(x => x.GetRepository<TestHidingDeleteUser, UserIdentifierType>()).Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = new TestDeleteUserHandler(unitOfWork.Object);
        return (sut, new HandlerMocks(unitOfWork, repository));
    }
}

/// <summary>Concrete subclass standing in for an app's <c>DeleteUserHandler</c> plus its erasure tail.</summary>
public sealed class TestDeleteUserHandler(IUnitOfWork unitOfWork)
    : DeleteUserHandlerBase<TestHidingDeleteUser, TestDeleteUserCommand>(unitOfWork, NullLogger.Instance)
{
    public int TailInvocations { get; private set; }

    public bool TailSawDeletedUser { get; private set; }

    public bool TailSawAnonymizedUser { get; private set; }

    public Result TailResult { get; set; } = Result.Success();

    public Action? OnTail { get; set; }

    public IReadOnlyList<Func<CancellationToken, Task>> PostCommitActions { get; set; } = [];

    protected override bool HasDeletePrivilege(string? currentUserRole) =>
        string.Equals(currentUserRole, "Organizer", StringComparison.OrdinalIgnoreCase);

    protected override Task<Result> OnAfterSoftDeleteAsync(
        TestHidingDeleteUser user,
        TestDeleteUserCommand command,
        ICollection<Func<CancellationToken, Task>> afterCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(afterCommit);

        TailInvocations++;
        TailSawDeletedUser = user.IsDeleted;
        TailSawAnonymizedUser = user.AnonymizeCalled;
        OnTail?.Invoke();

        foreach (var action in PostCommitActions)
        {
            afterCommit.Add(action);
        }

        return Task.FromResult(TailResult);
    }
}
