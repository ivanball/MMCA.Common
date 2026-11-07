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
/// Covers <c>GET {userId}/sign-ins</c> on <see cref="UsersAdminControllerBase{TUserDto}"/>: the
/// administrator's view of an account's sign-in history (T13). Like the sessions action it resolves
/// <see cref="IUserSessionsAdministrationService"/> per request and carries no weaker authorization
/// than the class's <see cref="AdministrationPermissions.ManageUsers"/>.
/// </summary>
public sealed class UsersAdminSignInsEndpointTests
{
    private const UserIdentifierType TargetUserId = 42;

    private static readonly MethodInfo SignInsAction =
        typeof(UsersAdminControllerBase<TestUserDto>).GetMethod(nameof(UsersAdminControllerBase<>.GetSignInsAsync))!;

    private readonly Mock<IUserAdministrationService<TestUserDto>> _users = new();
    private readonly Mock<IUserSessionsAdministrationService> _sessions = new();

    private TestUsersAdminController CreateUsersController() =>
        new(_users.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    [Fact]
    public async Task GetSignInsAsync_ReturnsTheServicesHistoryForTheRoutedUserAs200()
    {
        IReadOnlyList<SignInSummaryResponse> signIns =
        [
            new(Guid.NewGuid(), new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc), "198.51.100.4", "Firefox", null, null, IsCurrent: false),
        ];
        _sessions.Setup(x => x.GetSignInsAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(signIns));
        var sut = CreateUsersController();

        var result = await sut.GetSignInsAsync(TargetUserId, _sessions.Object);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(signIns);
        _sessions.Verify(x => x.GetSignInsAsync(TargetUserId, It.IsAny<CancellationToken>()), Times.Once);
        _users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSignInsAsync_Failure_ReturnsProblemDetails()
    {
        _sessions.Setup(x => x.GetSignInsAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<SignInSummaryResponse>>(Error.Forbidden("Admin.Denied", "Not allowed.")));
        var sut = CreateUsersController();

        var result = await sut.GetSignInsAsync(TargetUserId, _sessions.Object);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    [Fact]
    public void GetSignInsAsync_IsRoutedAsGetUserIdSignIns() =>
        SignInsAction.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("{userId}/sign-ins");

    [Fact]
    public void GetSignInsAsync_ResolvesTheSessionsServicePerAction() =>
        SignInsAction.GetParameters()
            .Single(p => p.ParameterType == typeof(IUserSessionsAdministrationService))
            .GetCustomAttribute<FromServicesAttribute>().Should().NotBeNull();

    // Spec A assertion 4 (admin half): the admin action requires users:manage.
    [Fact]
    public void GetSignInsAsync_RequiresUsersManage_AndNothingWeaker()
    {
        SignInsAction.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Should().BeEmpty();
        SignInsAction.GetCustomAttributes<HasPermissionAttribute>(inherit: true)
            .Select(a => a.Permission)
            .Should().OnlyContain(p => p == AdministrationPermissions.ManageUsers);
        SignInsAction.DeclaringType!.GetCustomAttribute<HasPermissionAttribute>()!.Permission
            .Should().Be(AdministrationPermissions.ManageUsers);
        AdministrationPermissions.ManageUsers.Should().Be("users:manage");
    }
}
