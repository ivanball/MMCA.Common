using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// Covers the opt-in administration controller bases: the Result-to-ActionResult mapping, the paging
/// conventions, and the capability attribute that is the whole protection on both surfaces.
/// </summary>
public sealed class AdministrationControllerBaseTests
{
    private const UserIdentifierType TargetUserId = 42;

    private readonly Mock<IUserAdministrationService<TestUserDto>> _users = new();
    private readonly Mock<IRoleAdministrationService> _roles = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    // ── Users: listing and paging ──
    [Fact]
    public async Task GetPagedAsync_PassesThePagingAndFilterThroughAndEchoesThePaginationHeader()
    {
        var page = new PagedCollectionResult<TestUserDto>(
            [new TestUserDto(TargetUserId, "user@example.com")],
            new PaginationMetadata(totalItemCount: 1, pageSize: 25, currentPage: 2));
        _users.Setup(x => x.ListAsync(It.IsAny<UserAdministrationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(page));
        var sut = CreateUsersController();

        var result = await sut.GetPagedAsync("ada", "Admin", pageNumber: 2, pageSize: 25);

        result.Result.Should().BeOfType<OkObjectResult>();
        sut.Response.Headers.Should().ContainKey("X-Pagination");
        JsonSerializer.Deserialize<PaginationMetadata>(sut.Response.Headers["X-Pagination"]!, JsonSerializerOptions.Web)!
            .CurrentPage.Should().Be(2);
        _users.Verify(
            x => x.ListAsync(
                It.Is<UserAdministrationQuery>(q =>
                    q.PageNumber == 2 && q.PageSize == 25 && q.SearchTerm == "ada" && q.Role == "Admin"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetPagedAsync_ClampsAnOversizedPageRequest()
    {
        _users.Setup(x => x.ListAsync(It.IsAny<UserAdministrationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PagedCollectionResult<TestUserDto>([], new PaginationMetadata())));
        var sut = CreateUsersController();

        await sut.GetPagedAsync(pageSize: 100_000);

        _users.Verify(
            x => x.ListAsync(
                It.Is<UserAdministrationQuery>(q => q.PageSize == UsersAdminControllerBase<TestUserDto>.MaxPageSize),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetPagedAsync_Failure_ReturnsProblemDetails()
    {
        _users.Setup(x => x.ListAsync(It.IsAny<UserAdministrationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PagedCollectionResult<TestUserDto>>(
                Error.Forbidden("Admin.Denied", "Not allowed.")));
        var sut = CreateUsersController();

        var result = await sut.GetPagedAsync();

        var objectResult = result.Result as ObjectResult;
        objectResult!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

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
        string[] roles = ["Admin", "Customer"];
        _users.Setup(x => x.SetRolesAsync(TargetUserId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var sut = CreateUsersController();

        ActionResult result = await sut.SetRolesAsync(TargetUserId, new SetUserRolesRequest(roles));

        result.Should().BeOfType<NoContentResult>();
        _users.Verify(
            x => x.SetRolesAsync(TargetUserId, It.Is<IReadOnlyList<string>>(r => r.Count == 2), It.IsAny<CancellationToken>()),
            Times.Once);
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
    public async Task GetRoleAsync_UnknownRole_ReturnsProblemDetails()
    {
        _roles.Setup(x => x.GetRoleAsync("Ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RolePermissionsResponse>(
                Error.NotFoundError("Authorization.RoleNotFound", "The role was not found.")));
        var sut = CreateRolesController();

        var result = await sut.GetAsync("Ghost");

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
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

    private TestUsersAdminController CreateUsersController() =>
        new(_users.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

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
