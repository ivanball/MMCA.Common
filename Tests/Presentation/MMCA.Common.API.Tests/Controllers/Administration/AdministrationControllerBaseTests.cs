using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// Covers the opt-in administration controller bases: the Result-to-ActionResult mapping
/// and the capability attribute that is the whole protection on both surfaces.
/// </summary>
public sealed class AdministrationControllerBaseTests
{
    private const UserIdentifierType TargetUserId = 42;

    private readonly Mock<IUserAdministrationService<TestUserDto>> _users = new();
    private readonly Mock<IRoleAdministrationService> _roles = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    // ── Users: read, lock, roles ──
    [Fact]
    public async Task GetAsync_Success_ReturnsTheAccount()
    {
        _users.Setup(x => x.GetAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TestUserDto(TargetUserId, "user@example.com")));
        var sut = CreateUsersController();

        var result = await sut.GetAsync(TargetUserId);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsProblemDetails()
    {
        _users.Setup(x => x.GetAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<TestUserDto>(Error.NotFound));
        var sut = CreateUsersController();

        var result = await sut.GetAsync(TargetUserId);

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockAndUnlock_DispatchTheMatchingIntent(bool locked)
    {
        _users.Setup(x => x.SetLockedAsync(TargetUserId, locked, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var sut = CreateUsersController();

        ActionResult result = locked
            ? await sut.LockAsync(TargetUserId)
            : await sut.UnlockAsync(TargetUserId);

        result.Should().BeOfType<NoContentResult>();
        _users.Verify(x => x.SetLockedAsync(TargetUserId, locked, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetRolesAsync_ReplacesTheWholeRoleSet()
    {
        string[] roles = ["Admin", "Member"];
        _users.Setup(x => x.SetRolesAsync(TargetUserId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var sut = CreateUsersController();

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest(roles));

        result.Should().BeOfType<NoContentResult>();
        _users.Verify(
            x => x.SetRolesAsync(TargetUserId, It.Is<IReadOnlyList<string>>(r => r.Count == 2), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Users: an administrator never locks, unlocks or re-roles their own account.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockAndUnlock_OnTheCallersOwnAccount_AreForbiddenAndNeverReachTheService(bool locked)
    {
        var sut = CreateUsersController(CallerPrincipal(TargetUserId));

        ActionResult result = locked
            ? await sut.LockAsync(TargetUserId)
            : await sut.UnlockAsync(TargetUserId);

        AssertSelfRefusal(result);
        _users.Verify(
            x => x.SetLockedAsync(It.IsAny<UserIdentifierType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetRolesAsync_OnTheCallersOwnAccount_IsForbiddenAndNeverReachesTheService()
    {
        var sut = CreateUsersController(CallerPrincipal(TargetUserId));

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest(["Member"]));

        AssertSelfRefusal(result);
        _users.Verify(
            x => x.SetRolesAsync(It.IsAny<UserIdentifierType>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LockAsync_BySignedInCallerOnAnotherAccount_StillLocks()
    {
        _users.Setup(x => x.SetLockedAsync(TargetUserId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var sut = CreateUsersController(CallerPrincipal(7));

        ActionResult result = await sut.LockAsync(TargetUserId);

        result.Should().BeOfType<NoContentResult>();
        _users.Verify(x => x.SetLockedAsync(TargetUserId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Roles ──
    [Fact]
    public async Task GetAllRolesAsync_ReturnsTheRoles()
    {
        _roles.Setup(x => x.ListRolesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RolePermissionsResponse>>(
                [new RolePermissionsResponse("Admin", ["orders:write"], ["reports:read"])]));
        var sut = CreateRolesController();

        var result = await sut.GetAllAsync();

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetCatalogAsync_ReturnsTheClosedSetsAnEditorRenders()
    {
        _roles.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(
                new PermissionCatalogResponse(["Admin", "Member"], ["orders:write", "reports:read"])));
        var sut = CreateRolesController();

        var result = await sut.GetCatalogAsync();

        var catalog = (result.Result as OkObjectResult)!.Value as PermissionCatalogResponse;
        catalog!.Roles.Should().Equal("Admin", "Member");
        catalog.Permissions.Should().Equal("orders:write", "reports:read");
    }

    [Fact]
    public async Task GetCatalogAsync_Failure_ReturnsProblemDetails()
    {
        _roles.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PermissionCatalogResponse>(
                Error.Forbidden("Admin.Denied", "Not allowed.")));
        var sut = CreateRolesController();

        var result = await sut.GetCatalogAsync();

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task GetRoleAsync_UnknownRole_ReturnsProblemDetails()
    {
        _roles.Setup(x => x.GetRoleAsync("Ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RolePermissionsResponse>(
                Error.NotFoundError("Authorization.RoleNotFound", "The role was not found.")));
        var sut = CreateRolesController();

        var result = await sut.GetAsync("Ghost");

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The stored-permission refusals reach the client as 400s with the service's own code, which is
    /// what lets an editor tell "you cannot store that" apart from "that role does not exist".
    /// </summary>
    /// <param name="code">The refusal the service produced.</param>
    [Theory]
    [InlineData("PermissionGrant.ManageRolesMustBeCompiled")]
    [InlineData("PermissionGrant.UnknownPermission")]
    public async Task SetPermissionsAsync_RefusedSet_ReturnsBadRequestProblemDetails(string code)
    {
        _roles.Setup(x => x.SetStoredPermissionsAsync(
                "Admin", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RolePermissionsResponse>(Error.Validation(code, "Refused.")));
        var sut = CreateRolesController();

        var result = await sut.SetPermissionsAsync("Admin", new SetRolePermissionsRequest(["roles:manage"]));

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SetPermissionsAsync_RecordsTheCallerAndReturnsTheRoleAfterTheEdit()
    {
        _currentUser.Setup(x => x.UserId).Returns(7);
        _roles.Setup(x => x.SetStoredPermissionsAsync(
                "Admin", It.IsAny<IReadOnlyList<string>>(), "7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RolePermissionsResponse("Admin", [], ["reports:read"])));
        var sut = CreateRolesController();

        var result = await sut.SetPermissionsAsync("Admin", new SetRolePermissionsRequest(["reports:read"]));

        result.Result.Should().BeOfType<OkObjectResult>();
        _roles.Verify(
            x => x.SetStoredPermissionsAsync(
                "Admin", It.IsAny<IReadOnlyList<string>>(), "7", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetPermissionsAsync_WithAnAnonymousCaller_RecordsNoAuditLabel()
    {
        _currentUser.Setup(x => x.UserId).Returns((UserIdentifierType?)null);
        _roles.Setup(x => x.SetStoredPermissionsAsync(
                "Admin", It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RolePermissionsResponse("Admin", [], [])));
        var sut = CreateRolesController();

        var result = await sut.SetPermissionsAsync("Admin", new SetRolePermissionsRequest([]));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    // ── The capability attributes are the protection ──
    [Fact]
    public void UsersAdminControllerBase_RequiresTheManageUsersPermission() =>
        typeof(UsersAdminControllerBase<TestUserDto>)
            .GetCustomAttribute<HasPermissionAttribute>()!.Permission
            .Should().Be(
                AdministrationPermissions.ManageUsers,
                "the capability attribute is the whole protection on a surface that reads and edits every account");

    /// <summary>
    /// The base declares the idempotency intent of its two POST actions, so an app controller that only
    /// inherits them satisfies the POST-intent fitness rule without an override.
    /// </summary>
    /// <param name="action">The base action name.</param>
    [Theory]
    [InlineData(nameof(UsersAdminControllerBase<>.LockAsync))]
    [InlineData(nameof(UsersAdminControllerBase<>.UnlockAsync))]
    public void LockAndUnlock_DeclareNonIdempotentOnTheBase(string action) =>
        typeof(TestUsersAdminController).GetMethod(action)!
            .GetCustomAttribute<NonIdempotentAttribute>(inherit: true)!.Justification
            .Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void RolesAdminControllerBase_RequiresTheManageRolesPermission() =>
        typeof(RolesAdminControllerBase)
            .GetCustomAttribute<HasPermissionAttribute>()!.Permission
            .Should().Be(AdministrationPermissions.ManageRoles);

    [Fact]
    public void AdministrationPermissions_AreDistinct() =>
        AdministrationPermissions.ManageUsers.Should().NotBe(
            AdministrationPermissions.ManageRoles,
            "granting a role editor the user surface, or the reverse, must be a separate decision");

    private static ClaimsPrincipal CallerPrincipal(UserIdentifierType userId) =>
        new(new ClaimsIdentity(
            [new Claim(AuthClaimTypes.Subject, userId.ToString(CultureInfo.InvariantCulture))],
            "Bearer"));

    private static void AssertSelfRefusal(ActionResult result)
    {
        var refusal = result.Should().BeAssignableTo<ObjectResult>().Which;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = refusal.Value.Should().BeOfType<ProblemDetails>().Which;
        JsonSerializer.Serialize(problem.Extensions["errors"]).Should().Contain("UserAdministration.Self");
    }

    private TestUsersAdminController CreateUsersController() =>
        new(_users.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private TestUsersAdminController CreateUsersController(ClaimsPrincipal user) =>
        new(_users.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };

    private TestRolesAdminController CreateRolesController() =>
        new(_roles.Object, _currentUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
}

/// <summary>
/// Stand-in for an app's administration user DTO (the real ones stay app-side). Public because Moq
/// proxies <c>IUserAdministrationService&lt;TestUserDto&gt;</c> over it.
/// </summary>
public sealed record TestUserDto(UserIdentifierType Id, string Email);

internal sealed class TestUsersAdminController(IUserAdministrationService<TestUserDto> administration)
    : UsersAdminControllerBase<TestUserDto>(administration);

internal sealed class TestRolesAdminController(
    IRoleAdministrationService administration,
    ICurrentUserService currentUserService)
    : RolesAdminControllerBase(administration, currentUserService);
