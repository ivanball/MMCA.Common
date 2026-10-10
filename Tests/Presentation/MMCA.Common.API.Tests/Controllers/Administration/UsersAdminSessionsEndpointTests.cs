using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// Covers <c>GET {userId}/sessions</c> on <see cref="UsersAdminControllerBase{TUserDto}"/>: the
/// administrator's read-only view of an account's live sessions. The action resolves
/// <see cref="IUserSessionsAdministrationService"/> per request (<c>[FromServices]</c>) so neither the
/// controller base constructor nor the consumer-implemented <c>IUserAdministrationService</c> changes,
/// returns the service's list as 200, and carries no weaker authorization than the class's
/// <see cref="AdministrationPermissions.ManageUsers"/>.
/// </summary>
public sealed class UsersAdminSessionsEndpointTests
{
    private const UserIdentifierType TargetUserId = 42;

    private static readonly MethodInfo SessionsAction =
        typeof(UsersAdminControllerBase<TestUserDto>).GetMethod(nameof(UsersAdminControllerBase<>.GetSessionsAsync))!;

    private readonly Mock<IUserAdministrationService<TestUserDto>> _users = new();
    private readonly Mock<IUserSessionsAdministrationService> _sessions = new();

    private static IReadOnlyList<RefreshSessionSummaryResponse> TwoLiveSessions() =>
    [
        new(Guid.NewGuid(), new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc), "198.51.100.4", "AtlDevCon/1.9.2 (Android 15; MmcaApp)", IsCurrent: false),
        new(Guid.NewGuid(), new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), null, null, IsCurrent: false),
    ];

    private TestUsersAdminController CreateUsersController() =>
        new(_users.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    // == Behavior ==
    [Fact]
    public async Task GetSessionsAsync_ReturnsTheServicesListAs200()
    {
        var sessions = TwoLiveSessions();
        _sessions.Setup(x => x.GetSessionsAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(sessions));
        var sut = CreateUsersController();

        var result = await sut.GetSessionsAsync(TargetUserId, _sessions.Object);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeSameAs(sessions);
    }

    [Fact]
    public async Task GetSessionsAsync_AsksTheServiceForTheRoutedUser()
    {
        _sessions.Setup(x => x.GetSessionsAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RefreshSessionSummaryResponse>>([]));
        var sut = CreateUsersController();

        await sut.GetSessionsAsync(TargetUserId, _sessions.Object);

        _sessions.Verify(x => x.GetSessionsAsync(TargetUserId, It.IsAny<CancellationToken>()), Times.Once);
        _users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSessionsAsync_WithNoLiveSessions_Returns200WithAnEmptyList()
    {
        _sessions.Setup(x => x.GetSessionsAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RefreshSessionSummaryResponse>>([]));
        var sut = CreateUsersController();

        var result = await sut.GetSessionsAsync(TargetUserId, _sessions.Object);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<RefreshSessionSummaryResponse>>().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionsAsync_Failure_ReturnsProblemDetails()
    {
        _sessions.Setup(x => x.GetSessionsAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<RefreshSessionSummaryResponse>>(
                Error.Forbidden("Admin.Denied", "Not allowed.")));
        var sut = CreateUsersController();

        var result = await sut.GetSessionsAsync(TargetUserId, _sessions.Object);

        var objectResult = result.Result as ObjectResult;
        objectResult!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // == Routing and the per-action service ==
    [Fact]
    public void GetSessionsAsync_IsRoutedAsGetUserIdSessions() =>
        SessionsAction.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("{userId}/sessions");

    [Fact]
    public void GetSessionsAsync_ResolvesTheSessionsServicePerAction()
    {
        var serviceParameter = SessionsAction.GetParameters()
            .Single(p => p.ParameterType == typeof(IUserSessionsAdministrationService));

        serviceParameter.GetCustomAttribute<FromServicesAttribute>().Should().NotBeNull(
            "the service is resolved per action so the controller base constructor does not change");
    }

    // == Authorization: no weaker than the class ==
    [Fact]
    public void GetSessionsAsync_DoesNotAllowAnonymousAccess() =>
        SessionsAction.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Should().BeEmpty(
            "the class-level ManageUsers permission is the whole protection on another user's device list");

    [Fact]
    public void GetSessionsAsync_DeclaresNoPermissionOtherThanManageUsers() =>
        SessionsAction.GetCustomAttributes<HasPermissionAttribute>(inherit: true)
            .Select(a => a.Permission)
            .Should().OnlyContain(p => p == AdministrationPermissions.ManageUsers);

    [Fact]
    public void GetSessionsAsync_InheritsTheClassLevelManageUsersRequirement() =>
        SessionsAction.DeclaringType!.GetCustomAttribute<HasPermissionAttribute>()!.Permission
            .Should().Be(AdministrationPermissions.ManageUsers);
}
