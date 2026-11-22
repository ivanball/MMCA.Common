using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Application.Auth.SoftDeletedUsers;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Tests.Auth.SignIn;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Administration;

/// <summary>
/// Pins security review 2026-11-21 item A2: with a permission registry, no administrator assigns a role
/// that grants a permission they do not hold, or changes, locks or unlocks an account that holds one.
/// The refusal is a Forbidden error with a stable code that names no permission. A caller holding every
/// permission is unaffected, and a role-only host (no registry) keeps the unrestricted behavior. Also
/// pins the acting administrator's id on the role-change and lock log lines (item A4).
/// </summary>
public sealed class UserAdministrationPrivilegeCeilingTests
{
    private const UserIdentifierType TargetUserId = 7;
    private const UserIdentifierType ActorUserId = 500;
    private const string UsersManage = "users:manage";
    private const string RolesManage = "roles:manage";

    private static readonly DateTimeOffset FixedNow = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<AdminTestUser, UserIdentifierType>> _repository = new();
    private readonly Mock<ISoftDeletedUserMarkerWriter> _markerWriter = new();
    private readonly InMemoryRefreshSessionStore _sessions = new();
    private readonly CapturingLogger _logger = new();
    private readonly RefreshSession _targetSession;

    public UserAdministrationPrivilegeCeilingTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<AdminTestUser, UserIdentifierType>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.GetReadRepository<AdminTestUser, UserIdentifierType>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _markerWriter
            .Setup(w => w.MarkDeletedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _markerWriter
            .Setup(w => w.ClearMarkerAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var now = FixedNow.UtcDateTime;
        _targetSession = _sessions.SeedLive(TargetUserId, "target-live", now.AddHours(-1), now.AddDays(30));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Admin holds both permissions, UserManager only users:manage, Member none.
    private static PermissionRegistry Registry() => new(new Dictionary<string, IReadOnlySet<string>>
    {
        ["Admin"] = new HashSet<string>(StringComparer.Ordinal) { UsersManage, RolesManage },
        ["UserManager"] = new HashSet<string>(StringComparer.Ordinal) { UsersManage },
        ["Member"] = new HashSet<string>(StringComparer.Ordinal),
    });

    // ---- SetRolesAsync ----
    [Fact]
    public async Task SetRolesAsync_CallerWithoutRolesManage_CannotAssignARoleThatCarriesIt()
    {
        var target = ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result result = await sut.SetRolesAsync(TargetUserId, ["Admin"], Ct);

        AssertCeilingRefusal(result);
        target.Role.Should().Be("Member");
        VerifyNothingSaved();
        _targetSession.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task SetRolesAsync_CallerWithoutRolesManage_CannotChangeTheRoleOfAnAccountThatHoldsIt()
    {
        var target = ArrangeTarget(role: "Admin");
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result result = await sut.SetRolesAsync(TargetUserId, ["Member"], Ct);

        AssertCeilingRefusal(result);
        target.Role.Should().Be("Admin", "demoting an account above the caller is still administering it");
        VerifyNothingSaved();
        _targetSession.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task SetRolesAsync_CallerWithoutRolesManage_CanStillAssignARoleWithinTheirOwnPermissions()
    {
        var target = ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result result = await sut.SetRolesAsync(TargetUserId, ["UserManager"], Ct);

        result.IsSuccess.Should().BeTrue("the ceiling refuses only what exceeds the caller");
        target.Role.Should().Be("UserManager");
    }

    [Fact]
    public async Task SetRolesAsync_CallerHoldingEveryPermission_CanAssignTheTopRole()
    {
        var target = ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("Admin"), Registry());

        Result result = await sut.SetRolesAsync(TargetUserId, ["Admin"], Ct);

        result.IsSuccess.Should().BeTrue();
        target.Role.Should().Be("Admin");
        _targetSession.IsRevoked.Should().BeTrue("a real role change still revokes the sessions");
    }

    [Fact]
    public async Task SetRolesAsync_CallerHoldingTheMissingPermissionAsAClaim_CanAssignTheRole()
    {
        var target = ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("UserManager", RolesManage), Registry());

        Result result = await sut.SetRolesAsync(TargetUserId, ["Admin"], Ct);

        result.IsSuccess.Should().BeTrue("a permission claim on the caller's token counts as holding it");
        target.Role.Should().Be("Admin");
    }

    [Fact]
    public async Task SetRolesAsync_CallerHoldingTheMissingPermissionThroughAStoredGrant_CanAssignTheRole()
    {
        var target = ArrangeTarget(role: "Member");
        var grants = new Mock<IPermissionGrantCache>();
        grants.Setup(g => g.GetPermissions(It.IsAny<string>())).Returns(new HashSet<string>(StringComparer.Ordinal));
        grants
            .Setup(g => g.GetPermissions("UserManager"))
            .Returns(new HashSet<string>(StringComparer.Ordinal) { RolesManage });
        var sut = CreateSut(Caller("UserManager"), new LayeredPermissionRegistry(Registry(), grants.Object));

        Result result = await sut.SetRolesAsync(TargetUserId, ["Admin"], Ct);

        result.IsSuccess.Should().BeTrue("a stored grant to the caller's role counts as holding the permission");
        target.Role.Should().Be("Admin");
    }

    [Fact]
    public async Task SetRolesAsync_OnARoleOnlyHost_IsUnrestricted()
    {
        var target = ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("UserManager"), permissions: null);

        Result result = await sut.SetRolesAsync(TargetUserId, ["Admin"], Ct);

        result.IsSuccess.Should().BeTrue("with no permission registry there is nothing to compare, so behavior is unchanged");
        target.Role.Should().Be("Admin");
    }

    // ---- SetLockedAsync ----
    [Fact]
    public async Task SetLockedAsync_CallerWithoutRolesManage_CannotLockAnAccountThatHoldsIt()
    {
        var target = ArrangeTarget(role: "Admin");
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result result = await sut.SetLockedAsync(TargetUserId, locked: true, Ct);

        AssertCeilingRefusal(result);
        target.Locked.Should().BeFalse();
        VerifyNothingSaved();
        _markerWriter.Verify(w => w.MarkDeletedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);
        _targetSession.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task SetLockedAsync_CallerWithoutRolesManage_CannotUnlockAnAccountThatHoldsIt()
    {
        var target = ArrangeTarget(role: "Admin", locked: true);
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result result = await sut.SetLockedAsync(TargetUserId, locked: false, Ct);

        AssertCeilingRefusal(result);
        target.Locked.Should().BeTrue();
        VerifyNothingSaved();
        _markerWriter.Verify(w => w.ClearMarkerAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetLockedAsync_CallerWithoutRolesManage_CanLockAnAccountWithinTheirOwnPermissions()
    {
        var target = ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result result = await sut.SetLockedAsync(TargetUserId, locked: true, Ct);

        result.IsSuccess.Should().BeTrue();
        target.Locked.Should().BeTrue();
    }

    [Fact]
    public async Task SetLockedAsync_CallerHoldingEveryPermission_CanLockTheTopRole()
    {
        var target = ArrangeTarget(role: "Admin");
        var sut = CreateSut(Caller("Admin"), Registry());

        Result result = await sut.SetLockedAsync(TargetUserId, locked: true, Ct);

        result.IsSuccess.Should().BeTrue();
        target.Locked.Should().BeTrue();
        _targetSession.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task SetLockedAsync_OnARoleOnlyHost_IsUnrestricted()
    {
        var target = ArrangeTarget(role: "Admin");
        var sut = CreateSut(Caller("UserManager"), permissions: null);

        Result result = await sut.SetLockedAsync(TargetUserId, locked: true, Ct);

        result.IsSuccess.Should().BeTrue();
        target.Locked.Should().BeTrue();
    }

    [Fact]
    public async Task TheRefusal_IsTheSameStableErrorForEveryShape_AndNamesNoPermission()
    {
        ArrangeTarget(role: "Admin");
        var sut = CreateSut(Caller("UserManager"), Registry());

        Result assign = await sut.SetRolesAsync(TargetUserId, ["Admin"], Ct);
        Result demote = await sut.SetRolesAsync(TargetUserId, ["Member"], Ct);
        Result lockIt = await sut.SetLockedAsync(TargetUserId, locked: true, Ct);

        var codes = new[] { assign, demote, lockIt }.Select(r => r.Errors.Should().ContainSingle().Subject.Code).Distinct().ToList();
        codes.Should().ContainSingle("the refusal carries one stable code whichever check refused");
        codes[0].Should().NotBeNullOrWhiteSpace();
    }

    // ---- Acting administrator on the change lines (A4) ----
    [Fact]
    public async Task SetRolesAsync_LogsTheActingAdministratorsUserId()
    {
        ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("Admin"), Registry());

        (await sut.SetRolesAsync(TargetUserId, ["UserManager"], Ct)).IsSuccess.Should().BeTrue();

        _logger.Lines.Should().Contain(
            l => l.Message.Contains(ActorUserId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                && l.Message.Contains("UserManager", StringComparison.Ordinal),
            "the role-change line names the administrator who made it");
    }

    [Fact]
    public async Task SetLockedAsync_LogsTheActingAdministratorsUserId()
    {
        ArrangeTarget(role: "Member");
        var sut = CreateSut(Caller("Admin"), Registry());

        (await sut.SetLockedAsync(TargetUserId, locked: true, Ct)).IsSuccess.Should().BeTrue();

        _logger.Lines.Should().Contain(
            l => l.Level == LogLevel.Information
                && l.Message.Contains(ActorUserId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "the lock line names the administrator who made it");
    }

    // ---- Helpers ----
    private static void AssertCeilingRefusal(Result result)
    {
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Type.Should().Be(ErrorType.Forbidden);
        error.Code.Should().NotBeNullOrWhiteSpace();
        error.Message.Should().NotContain(RolesManage, "the refusal does not say which permission was missing");
        error.Message.Should().NotContain(UsersManage);
    }

    private static ICurrentUserService Caller(string role, params string[] permissionClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        claims.AddRange(permissionClaims.Select(p => new Claim(AuthClaimTypes.Permission, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        var current = new Mock<ICurrentUserService>();
        current.SetupGet(c => c.User).Returns(principal);
        current.SetupGet(c => c.UserId).Returns(ActorUserId);
        current.SetupGet(c => c.Role).Returns(role);
        return current.Object;
    }

    private AdminTestUser ArrangeTarget(string role, bool locked = false)
    {
        var user = new AdminTestUser { Id = TargetUserId, Role = role, Locked = locked };
        _repository.Setup(r => r.GetByIdAsync(TargetUserId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        return user;
    }

    private CeilingUserAdministrationService CreateSut(ICurrentUserService currentUser, IPermissionRegistry? permissions) =>
        new(_unitOfWork.Object, _sessions, new FakeTimeProvider(FixedNow), _markerWriter.Object, _logger, currentUser, permissions);

    private void VerifyNothingSaved() =>
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

    /// <summary>An app-side subclass that opts into the privilege ceiling.</summary>
    private sealed class CeilingUserAdministrationService(
        IUnitOfWork unitOfWork,
        IRefreshSessionStore refreshSessions,
        TimeProvider timeProvider,
        ISoftDeletedUserMarkerWriter markerWriter,
        ILogger logger,
        ICurrentUserService currentUser,
        IPermissionRegistry? permissions)
        : UserAdministrationServiceBase<AdminTestUser, AdminTestUserDto>(
            unitOfWork, refreshSessions, timeProvider, markerWriter, logger, currentUser, permissions)
    {
        protected override AdminTestUserDto MapToDto(AdminTestUser user) => new(user.Id, user.Role, user.Locked, "mapped");

        protected override bool IsLocked(AdminTestUser user) => user.Locked;

        protected override Result ApplyLockState(AdminTestUser user, bool locked, DateTime utcNow)
        {
            user.Locked = locked;
            return Result.Success();
        }

        protected override string GetRole(AdminTestUser user) => user.Role;

        protected override Result ApplyRole(AdminTestUser user, string role)
        {
            user.Role = role;
            return Result.Success();
        }

        protected override Error CreateExactlyOneRoleError() =>
            Error.Validation("App.ExactlyOneRole", "Exactly one role is required.", "SetRolesAsync", "Roles");
    }
}
