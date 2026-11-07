using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Tests.Controllers.Auth;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// The grant action on <see cref="UsersAdminControllerBase{TUserDto}"/> and the end action on
/// <see cref="AuthControllerBase"/> (T20, ADR-137).
/// </summary>
public sealed class ViewAsEndpointTests
{
    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;

    private static readonly MethodInfo StartAction =
        typeof(UsersAdminControllerBase<TestUserDto>).GetMethod(nameof(UsersAdminControllerBase<>.StartViewAsAsync))!;

    private readonly Mock<IViewAsService> _viewAs = new(MockBehavior.Strict);

    // Spec B assertion 5: with ViewAs:Enabled false the grant endpoint returns 404.
    [Fact]
    public async Task StartViewAsAsync_WhenViewAsIsDisabled_Returns404_AndIssuesNothing()
    {
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), Settings(enabled: false), _viewAs.Object);

        result.Result.Should().BeOfType<NotFoundResult>();
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EndViewAsAsync_WhenViewAsIsDisabled_Returns404()
    {
        var sut = CreateAuthController(ViewAsPrincipal(Guid.NewGuid()));

        var result = await sut.EndViewAsAsync(Settings(enabled: false), _viewAs.Object, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StartViewAsAsync_WhenEnabled_ReturnsTheGrantForTheCallerAndTheRoutedAccount()
    {
        var response = new ViewAsGrantResponse("view-as-token", DateTime.UtcNow.AddMinutes(15), Guid.NewGuid(), TargetUserId);
        _viewAs.Setup(v => v.StartAsync(StaffUserId, TargetUserId, "Ticket 4411: schedule missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(response));
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), Settings(enabled: true), _viewAs.Object);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task StartViewAsAsync_ARefusal_IsProblemDetails()
    {
        _viewAs.Setup(v => v.StartAsync(StaffUserId, TargetUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ViewAsGrantResponse>(Error.Forbidden("ViewAs.TargetIsStaff", "No.")));
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), Settings(enabled: true), _viewAs.Object);

        result.Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task EndViewAsAsync_EndsTheGrantNamedByTheToken()
    {
        var grantId = Guid.NewGuid();
        _viewAs.Setup(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
        var sut = CreateAuthController(ViewAsPrincipal(grantId));

        var result = await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _viewAs.Verify(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndViewAsAsync_ForAnOrdinaryToken_IsABadRequest()
    {
        var sut = CreateAuthController(StaffPrincipal());

        var result = await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public void StartViewAsAsync_IsRoutedAsPostUserIdViewAs_AndRequiresUsersViewAsOnTopOfUsersManage()
    {
        StartAction.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{userId}/view-as");
        StartAction.GetCustomAttribute<HasPermissionAttribute>()!.Permission.Should().Be("users:view-as");
        StartAction.DeclaringType!.GetCustomAttribute<HasPermissionAttribute>()!.Permission.Should().Be(AdministrationPermissions.ManageUsers);
        StartAction.GetCustomAttribute<NonIdempotentAttribute>().Should().NotBeNull();
    }

    private static IOptions<ViewAsSettings> Settings(bool enabled) => Options.Create(new ViewAsSettings { Enabled = enabled });

    private static ClaimsPrincipal StaffPrincipal() =>
        new(new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, "1")], "Bearer"));

    private static ClaimsPrincipal ViewAsPrincipal(Guid grantId) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "2"),
            new Claim(AuthClaimTypes.ViewAsActor, "1"),
            new Claim(AuthClaimTypes.ViewAsGrant, grantId.ToString()),
        ],
        "Bearer"));

    private static TestUsersAdminController CreateUsersController(ClaimsPrincipal user) =>
        new(Mock.Of<IUserAdministrationService<TestUserDto>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    private static TestAuthController CreateAuthController(ClaimsPrincipal user) =>
        new(Mock.Of<IAuthenticationService>(), Mock.Of<ICurrentUserService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };
}
