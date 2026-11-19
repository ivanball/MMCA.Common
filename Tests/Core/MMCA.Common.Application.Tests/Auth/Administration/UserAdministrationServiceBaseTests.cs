using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.SoftDeletedUsers;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Administration;

/// <summary>
/// Pins the shared administration workflow in <see cref="UserAdministrationServiceBase{TUser, TUserDto}"/>
/// against its contract: a lock is saved, marks the account in the soft-deleted user cache and revokes its
/// live refresh sessions; an unlock lifts the marker only when it lifted a lock (the same marker carries a
/// revoked machine client's block on its service account); the marker write and clear are best effort; a
/// role change takes exactly one role, validates before loading, and is a no-op when the role is unchanged.
/// </summary>
public sealed class UserAdministrationServiceBaseTests
{
    private const UserIdentifierType KnownUserId = 7;
    private const UserIdentifierType OtherUserId = 8;
    private const UserIdentifierType UnknownUserId = 404;

    private static readonly DateTimeOffset FixedNow = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<AdminTestUser, UserIdentifierType>> _repository = new();
    private readonly Mock<ISoftDeletedUserMarkerWriter> _markerWriter = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly InMemoryRefreshSessionStore _sessions = new();
    private readonly FakeTimeProvider _time = new(FixedNow);

    private readonly RefreshSession _userSession;
    private readonly RefreshSession _otherUserSession;

    public UserAdministrationServiceBaseTests()
    {
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _unitOfWork.Setup(u => u.GetRepository<AdminTestUser, UserIdentifierType>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.GetReadRepository<AdminTestUser, UserIdentifierType>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository
            .Setup(r => r.GetByIdAsync(UnknownUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminTestUser?)null);
        _markerWriter
            .Setup(w => w.MarkDeletedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _markerWriter
            .Setup(w => w.ClearMarkerAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var now = FixedNow.UtcDateTime;
        _userSession = SeedSession(KnownUserId, "known-live", now.AddHours(-1), now.AddDays(30));
        _otherUserSession = SeedSession(OtherUserId, "other-live", now.AddHours(-1), now.AddDays(30));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- GetAsync ----
    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNotFound()
    {
        var result = await CreateSut().GetAsync(UnknownUserId, Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task GetAsync_KnownId_ReturnsTheHooksDto()
    {
        var user = ArrangeUser(role: "Manager", locked: true);

        var result = await CreateSut().GetAsync(KnownUserId, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new AdminTestUserDto(KnownUserId, "Manager", true, "mapped"));
        result.Value!.Id.Should().Be(user.Id);
    }

    // ---- SetLockedAsync(lock = true) ----
    [Fact]
    public async Task SetLockedAsync_Lock_UnknownId_ReturnsNotFoundAndSavesNothing()
    {
        var result = await CreateSut().SetLockedAsync(UnknownUserId, locked: true, Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.NotFound);
        VerifyNotSaved();
        VerifyNoMarkerWritten();
        VerifyNoMarkerCleared();
    }

    [Fact]
    public async Task SetLockedAsync_Lock_WhenApplyLockStateFails_ReturnsThatFailureWithNoSaveNoMarkerNoRevocation()
    {
        var user = ArrangeUser();
        var refusal = Error.Invariant("User.CannotLock", "This account cannot be locked.", "AdminTestUser");
        var sut = CreateSut();
        sut.LockResult = Result.Failure(refusal);

        var result = await sut.SetLockedAsync(KnownUserId, locked: true, Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Equal(refusal);
        user.Locked.Should().BeFalse();
        VerifyNotSaved();
        VerifyNoMarkerWritten();
        _userSession.IsRevoked.Should().BeFalse("a refused lock must not sign the account out");
    }

    [Fact]
    public async Task SetLockedAsync_Lock_Success_SavesOnceWritesTheMarkerAndRevokesTheUsersLiveSessions()
    {
        var user = ArrangeUser();

        var result = await CreateSut().SetLockedAsync(KnownUserId, locked: true, Ct);

        result.IsSuccess.Should().BeTrue();
        user.Locked.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _markerWriter.Verify(w => w.MarkDeletedAsync(KnownUserId, It.IsAny<CancellationToken>()), Times.Once);
        _markerWriter.Verify(w => w.MarkDeletedAsync(It.Is<UserIdentifierType>(id => id != KnownUserId), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoMarkerCleared();
        _userSession.IsRevoked.Should().BeTrue();
        _otherUserSession.IsRevoked.Should().BeFalse("only the locked account's sessions are revoked");
    }

    // ---- SetLockedAsync(lock = false) ----
    [Fact]
    public async Task SetLockedAsync_Unlock_OnALockedAccount_SavesAndClearsTheMarkerWithoutWritingOne()
    {
        var user = ArrangeUser(locked: true);

        var result = await CreateSut().SetLockedAsync(KnownUserId, locked: false, Ct);

        result.IsSuccess.Should().BeTrue();
        user.Locked.Should().BeFalse();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _markerWriter.Verify(w => w.ClearMarkerAsync(KnownUserId, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoMarkerWritten();
    }

    [Fact]
    public async Task SetLockedAsync_Unlock_OnAnAccountThatWasNotLocked_LeavesTheMarkerAlone()
    {
        ArrangeUser(locked: false);

        var result = await CreateSut().SetLockedAsync(KnownUserId, locked: false, Ct);

        result.IsSuccess.Should().BeTrue();
        VerifyNoMarkerCleared();
        VerifyNoMarkerWritten();
    }

    // ---- Marker best effort ----
    [Fact]
    public async Task SetLockedAsync_Lock_WhenTheMarkerWriteThrows_StillSucceedsAndLogsAWarning()
    {
        ArrangeUser();
        _markerWriter
            .Setup(w => w.MarkDeletedAsync(KnownUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));

        var result = await CreateSut().SetLockedAsync(KnownUserId, locked: true, Ct);

        result.IsSuccess.Should().BeTrue();
        VerifyWarningLogged<InvalidOperationException>();
    }

    [Fact]
    public async Task SetLockedAsync_Unlock_WhenTheMarkerClearThrows_StillSucceedsAndLogsAWarning()
    {
        ArrangeUser(locked: true);
        _markerWriter
            .Setup(w => w.ClearMarkerAsync(KnownUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));

        var result = await CreateSut().SetLockedAsync(KnownUserId, locked: false, Ct);

        result.IsSuccess.Should().BeTrue();
        VerifyWarningLogged<InvalidOperationException>();
    }

    [Fact]
    public async Task SetLockedAsync_Lock_WhenTheMarkerWriteIsCancelled_PropagatesTheCancellation()
    {
        ArrangeUser();
        _markerWriter
            .Setup(w => w.MarkDeletedAsync(KnownUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await CreateSut().SetLockedAsync(KnownUserId, locked: true, Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SetLockedAsync_Unlock_WhenTheMarkerClearIsCancelled_PropagatesTheCancellation()
    {
        ArrangeUser(locked: true);
        _markerWriter
            .Setup(w => w.ClearMarkerAsync(KnownUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await CreateSut().SetLockedAsync(KnownUserId, locked: false, Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- SetRolesAsync ----
    [Fact]
    public async Task SetRolesAsync_ZeroRoles_ReturnsExactlyTheAppsErrorAndLoadsNothing()
    {
        ArrangeUser();
        var sut = CreateSut();

        var result = await sut.SetRolesAsync(KnownUserId, [], Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Equal(TestUserAdministrationService.ExactlyOneRoleError);
        VerifyNothingLoaded();
        VerifyNotSaved();
    }

    [Fact]
    public async Task SetRolesAsync_TwoRoles_ReturnsExactlyTheAppsErrorAndLoadsNothing()
    {
        ArrangeUser();
        var sut = CreateSut();

        var result = await sut.SetRolesAsync(KnownUserId, ["Manager", "Member"], Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Equal(TestUserAdministrationService.ExactlyOneRoleError);
        VerifyNothingLoaded();
        VerifyNotSaved();
    }

    [Fact]
    public async Task SetRolesAsync_WhenValidateRequestedRoleFails_ReturnsItsErrorsBeforeLoading()
    {
        ArrangeUser();
        var first = Error.Validation("App.UnknownRole", "The role is not known.", "SetRolesAsync", "Roles");
        var second = Error.Validation("App.RoleRetired", "The role is retired.", "SetRolesAsync", "Roles");
        var sut = CreateSut();
        sut.RoleValidationResult = Result.Failure([first, second]);

        var result = await sut.SetRolesAsync(UnknownUserId, ["Ghost"], Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Equal(first, second);
        sut.ValidatedRoles.Should().Equal("Ghost");
        VerifyNothingLoaded();
        VerifyNotSaved();
    }

    [Fact]
    public async Task SetRolesAsync_UnknownId_ReturnsNotFound()
    {
        var result = await CreateSut().SetRolesAsync(UnknownUserId, ["Manager"], Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.NotFound);
        VerifyNotSaved();
    }

    [Fact]
    public async Task SetRolesAsync_WhenApplyRoleFails_ReturnsThatFailureWithNothingSaved()
    {
        var user = ArrangeUser(role: "Member");
        var refusal = Error.Validation("App.RoleNotAssignable", "That role cannot be assigned.", "AdminTestUser", "Role");
        var sut = CreateSut();
        sut.RoleResult = Result.Failure(refusal);

        var result = await sut.SetRolesAsync(KnownUserId, ["Manager"], Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Equal(refusal);
        user.Role.Should().Be("Member");
        VerifyNotSaved();
        _userSession.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task SetRolesAsync_UnchangedRole_SucceedsWithNoSaveAndNoRevocation()
    {
        ArrangeUser(role: "Manager");

        var result = await CreateSut().SetRolesAsync(KnownUserId, ["Manager"], Ct);

        result.IsSuccess.Should().BeTrue();
        VerifyNotSaved();
        _userSession.IsRevoked.Should().BeFalse("re-submitting the held role must not sign the account out");
    }

    [Fact]
    public async Task SetRolesAsync_CaseOnlyChange_IsAChangeUnderOrdinalComparison()
    {
        var user = ArrangeUser(role: "manager");

        var result = await CreateSut().SetRolesAsync(KnownUserId, ["Manager"], Ct);

        result.IsSuccess.Should().BeTrue();
        user.Role.Should().Be("Manager");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _userSession.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task SetRolesAsync_ChangedRole_SavesOnceAndRevokesTheUsersLiveSessions()
    {
        var user = ArrangeUser(role: "Member");

        var result = await CreateSut().SetRolesAsync(KnownUserId, ["Manager"], Ct);

        result.IsSuccess.Should().BeTrue();
        user.Role.Should().Be("Manager");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _userSession.IsRevoked.Should().BeTrue("the role travels in the access token");
        _otherUserSession.IsRevoked.Should().BeFalse();
    }

    // ---- Helpers ----
    private TestUserAdministrationService CreateSut() =>
        new(_unitOfWork.Object, _sessions, _time, _markerWriter.Object, _logger.Object);

    private AdminTestUser ArrangeUser(string role = "Member", bool locked = false)
    {
        var user = new AdminTestUser { Id = KnownUserId, Role = role, Locked = locked };
        _repository.Setup(r => r.GetByIdAsync(KnownUserId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        return user;
    }

    private RefreshSession SeedSession(UserIdentifierType userId, string token, DateTime createdAt, DateTime expiresAt)
    {
        var session = RefreshSession.Create(userId, token, createdAt, expiresAt, "203.0.113.7", "Mozilla/5.0 (test)").Value!;
        _sessions.Seed(session);
        return session;
    }

    private void VerifyNotSaved() =>
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNoMarkerWritten() =>
        _markerWriter.Verify(w => w.MarkDeletedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNoMarkerCleared() =>
        _markerWriter.Verify(w => w.ClearMarkerAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNothingLoaded()
    {
        _unitOfWork.Verify(u => u.GetRepository<AdminTestUser, UserIdentifierType>(), Times.Never);
        _unitOfWork.Verify(u => u.GetReadRepository<AdminTestUser, UserIdentifierType>(), Times.Never);
        _repository.Verify(r => r.GetByIdAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void VerifyWarningLogged<TException>()
        where TException : Exception =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception?>(e => e is TException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
}

/// <summary>A minimal user aggregate holding one role and a lock flag.</summary>
public sealed class AdminTestUser : AuditableAggregateRootEntity<UserIdentifierType>
{
    public string Role { get; set; } = "Member";

    public bool Locked { get; set; }
}

/// <summary>The administration DTO the test service maps to; <c>Marker</c> proves the hook produced it.</summary>
public sealed record AdminTestUserDto(UserIdentifierType Id, string Role, bool Locked, string Marker);

/// <summary>The smallest app-side subclass: every hook is configurable and records what it saw.</summary>
public sealed class TestUserAdministrationService(
    IUnitOfWork unitOfWork,
    IRefreshSessionStore refreshSessions,
    TimeProvider timeProvider,
    ISoftDeletedUserMarkerWriter markerWriter,
    ILogger logger)
    : UserAdministrationServiceBase<AdminTestUser, AdminTestUserDto>(unitOfWork, refreshSessions, timeProvider, markerWriter, logger)
{
    public static readonly Error ExactlyOneRoleError =
        Error.Validation("App.ExactlyOneRole", "Exactly one role is required.", "SetRolesAsync", "Roles");

    public Result LockResult { get; set; } = Result.Success();

    public Result RoleResult { get; set; } = Result.Success();

    public Result RoleValidationResult { get; set; } = Result.Success();

    public List<string> ValidatedRoles { get; } = [];

    protected override AdminTestUserDto MapToDto(AdminTestUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new AdminTestUserDto(user.Id, user.Role, user.Locked, "mapped");
    }

    protected override bool IsLocked(AdminTestUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Locked;
    }

    protected override Result ApplyLockState(AdminTestUser user, bool locked, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (LockResult.IsFailure)
        {
            return LockResult;
        }

        user.Locked = locked;
        return Result.Success();
    }

    protected override string GetRole(AdminTestUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Role;
    }

    protected override Result ApplyRole(AdminTestUser user, string role)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (RoleResult.IsFailure)
        {
            return RoleResult;
        }

        user.Role = role;
        return Result.Success();
    }

    protected override Result ValidateRequestedRole(string role)
    {
        ValidatedRoles.Add(role);
        return RoleValidationResult;
    }

    protected override Error CreateExactlyOneRoleError() => ExactlyOneRoleError;
}
