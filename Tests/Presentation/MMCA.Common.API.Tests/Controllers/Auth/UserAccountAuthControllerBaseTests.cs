using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.Users;
using MMCA.Common.Application.Users.UseCases.GetPreferences;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Auth;

public sealed class UserAccountAuthControllerBaseTests
{
    private const int UserId = 42;

    private readonly Mock<IAuthenticationService> _authServiceMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();
    private readonly Mock<ICommandHandler<TestChangePasswordCommand, Result>> _changePasswordHandlerMock = new();
    private readonly Mock<ICommandHandler<TestChangePreferencesCommand, Result>> _changePreferencesHandlerMock = new();
    private readonly Mock<IQueryHandler<GetUserPreferencesQuery, Result<UserPreferencesResponse>>> _getPreferencesHandlerMock = new();

    private TestUserAccountAuthController CreateController() =>
        new(
            _authServiceMock.Object,
            _currentUserServiceMock.Object,
            _changePasswordHandlerMock.Object,
            _changePreferencesHandlerMock.Object,
            _getPreferencesHandlerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    // ── ChangePasswordAsync ──
    [Fact]
    public async Task ChangePasswordAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.ChangePasswordAsync(
            new ChangePasswordRequest("old", "New-Password1!"),
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        _changePasswordHandlerMock.Verify(
            x => x.HandleAsync(It.IsAny<TestChangePasswordCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangePasswordAsync_Success_ReturnsNoContentAndDispatchesAppCommand()
    {
        var request = new ChangePasswordRequest("old", "New-Password1!");
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _changePasswordHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestChangePasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.ChangePasswordAsync(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _changePasswordHandlerMock.Verify(
            x => x.HandleAsync(
                It.Is<TestChangePasswordCommand>(c => c.UserId == UserId && c.Request == request),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ChangePasswordAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _changePasswordHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestChangePasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Unauthorized("Auth.InvalidCurrentPassword", "Current password is incorrect.")));
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.ChangePasswordAsync(
            new ChangePasswordRequest("wrong", "New-Password1!"),
            CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── ChangePreferencesAsync ──
    [Fact]
    public async Task ChangePreferencesAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.ChangePreferencesAsync(
            new ChangePreferencesRequest("es", null),
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        _changePreferencesHandlerMock.Verify(
            x => x.HandleAsync(It.IsAny<TestChangePreferencesCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangePreferencesAsync_Success_ReturnsNoContentAndDispatchesAppCommand()
    {
        var request = new ChangePreferencesRequest("es", "dark");
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _changePreferencesHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestChangePreferencesCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.ChangePreferencesAsync(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _changePreferencesHandlerMock.Verify(
            x => x.HandleAsync(
                It.Is<TestChangePreferencesCommand>(c => c.UserId == UserId && c.Request == request),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ChangePreferencesAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _changePreferencesHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestChangePreferencesCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Validation("User.InvalidCulture", "Culture is not supported.")));
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.ChangePreferencesAsync(
            new ChangePreferencesRequest("zz", null),
            CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── GetPreferencesAsync ──
    [Fact]
    public async Task GetPreferencesAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        TestUserAccountAuthController sut = CreateController();

        ActionResult<UserPreferencesResponse> result = await sut.GetPreferencesAsync(CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
        _getPreferencesHandlerMock.Verify(
            x => x.HandleAsync(It.IsAny<GetUserPreferencesQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPreferencesAsync_Success_ReturnsOkWithPreferences()
    {
        var preferences = new UserPreferencesResponse("es", "dark");
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _getPreferencesHandlerMock
            .Setup(x => x.HandleAsync(
                It.Is<GetUserPreferencesQuery>(q => q.UserId == UserId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(preferences));
        TestUserAccountAuthController sut = CreateController();

        ActionResult<UserPreferencesResponse> result = await sut.GetPreferencesAsync(CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(preferences);
    }

    [Fact]
    public async Task GetPreferencesAsync_Failure_ReturnsHandleFailure()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _getPreferencesHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<GetUserPreferencesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<UserPreferencesResponse>(
                Error.NotFoundError("User.NotFound", "User not found")));
        TestUserAccountAuthController sut = CreateController();

        ActionResult<UserPreferencesResponse> result = await sut.GetPreferencesAsync(CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── inherited AuthControllerBase behavior stays intact ──
    [Fact]
    public async Task InheritedLoginAsync_Success_StillReturnsOk()
    {
        var authResponse = new AuthenticationResponse("access-token", "refresh-token", DateTime.UtcNow.AddHours(1));
        var request = new LoginRequest("test@example.com", "Password123!");
        _authServiceMock.Setup(x => x.LoginAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponse));
        TestUserAccountAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.LoginAsync(request, CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.Value.Should().Be(authResponse);
    }

    [Fact]
    public async Task InheritedRevokeAsync_Success_StillReturnsNoContent()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns(UserId);
        _authServiceMock.Setup(x => x.RevokeAllSessionsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestUserAccountAuthController sut = CreateController();

        ActionResult result = await sut.RevokeAsync(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }
}

/// <summary>
/// Stand-in for an app change-password command (the real ones stay app-side). Public because Moq
/// proxies <c>ICommandHandler&lt;TestChangePasswordCommand, Result&gt;</c> over it.
/// </summary>
public sealed record TestChangePasswordCommand(UserIdentifierType UserId, ChangePasswordRequest Request)
    : IUserScopedCommand<ChangePasswordRequest>;

/// <summary>
/// Stand-in for an app change-preferences command (the real ones stay app-side). Public because Moq
/// proxies <c>ICommandHandler&lt;TestChangePreferencesCommand, Result&gt;</c> over it.
/// </summary>
public sealed record TestChangePreferencesCommand(UserIdentifierType UserId, ChangePreferencesRequest Request)
    : IUserScopedCommand<ChangePreferencesRequest>;

internal sealed class TestUserAccountAuthController(
    IAuthenticationService authenticationService,
    ICurrentUserService currentUserService,
    ICommandHandler<TestChangePasswordCommand, Result> changePasswordHandler,
    ICommandHandler<TestChangePreferencesCommand, Result> changePreferencesHandler,
    IQueryHandler<GetUserPreferencesQuery, Result<UserPreferencesResponse>> getUserPreferencesHandler)
    : UserAccountAuthControllerBase<TestChangePasswordCommand, TestChangePreferencesCommand>(
        authenticationService,
        currentUserService,
        changePasswordHandler,
        changePreferencesHandler,
        getUserPreferencesHandler)
{
    protected override TestChangePasswordCommand CreateChangePasswordCommand(
        UserIdentifierType userId,
        ChangePasswordRequest request) => new(userId, request);

    protected override TestChangePreferencesCommand CreateChangePreferencesCommand(
        UserIdentifierType userId,
        ChangePreferencesRequest request) => new(userId, request);
}
