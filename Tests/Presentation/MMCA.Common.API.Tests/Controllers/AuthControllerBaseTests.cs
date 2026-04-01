using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

public sealed class AuthControllerBaseTests
{
    private readonly Mock<IAuthenticationService> _authServiceMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();

    private TestAuthController CreateController() =>
        new(_authServiceMock.Object, _currentUserServiceMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    private static AuthenticationResponse CreateAuthResponse() =>
        new("access-token", "refresh-token", DateTime.UtcNow.AddHours(1));

    // ── LoginAsync ──
    [Fact]
    public async Task LoginAsync_Success_ReturnsOkWithAuthResponse()
    {
        AuthenticationResponse authResponse = CreateAuthResponse();
        var request = new LoginRequest("test@example.com", "Password123!");
        _authServiceMock.Setup(x => x.LoginAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponse));
        TestAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.LoginAsync(request, CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(authResponse);
    }

    [Fact]
    public async Task LoginAsync_Failure_ReturnsHandleFailure()
    {
        var request = new LoginRequest("test@example.com", "wrong");
        _authServiceMock.Setup(x => x.LoginAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidCredentials", "Invalid credentials")));
        TestAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.LoginAsync(request, CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── RegisterAsync ──
    [Fact]
    public async Task RegisterAsync_Success_Returns201Created()
    {
        AuthenticationResponse authResponse = CreateAuthResponse();
        var request = new RegisterRequest("new@example.com", "Password123!", "John", "Doe");
        _authServiceMock.Setup(x => x.RegisterAsync(request, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponse));
        TestAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.RegisterAsync(request, CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status201Created);
        objectResult.Value.Should().Be(authResponse);
    }

    [Fact]
    public async Task RegisterAsync_Failure_ReturnsHandleFailure()
    {
        var request = new RegisterRequest("existing@example.com", "Password123!", "John", "Doe");
        _authServiceMock.Setup(x => x.RegisterAsync(request, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Conflict("Auth.EmailTaken", "Email already registered")));
        TestAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.RegisterAsync(request, CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── RefreshAsync ──
    [Fact]
    public async Task RefreshAsync_Success_ReturnsOkWithAuthResponse()
    {
        AuthenticationResponse authResponse = CreateAuthResponse();
        var request = new RefreshTokenRequest("expired-access-token", "valid-refresh-token");
        _authServiceMock.Setup(x => x.RefreshTokenAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponse));
        TestAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.RefreshAsync(request, CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(authResponse);
    }

    [Fact]
    public async Task RefreshAsync_Failure_ReturnsHandleFailure()
    {
        var request = new RefreshTokenRequest("expired-access-token", "invalid-refresh-token");
        _authServiceMock.Setup(x => x.RefreshTokenAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidRefreshToken", "Refresh token is invalid")));
        TestAuthController sut = CreateController();

        ActionResult<AuthenticationResponse> result = await sut.RefreshAsync(request, CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── RevokeAsync ──
    [Fact]
    public async Task RevokeAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeAsync(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task RevokeAsync_Success_ReturnsNoContent()
    {
        const int userId = 42;
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock.Setup(x => x.RevokeTokenAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeAsync(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RevokeAsync_Failure_ReturnsHandleFailure()
    {
        const int userId = 42;
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock.Setup(x => x.RevokeTokenAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFoundError("Auth.UserNotFound", "User not found")));
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeAsync(CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── ChangePasswordAsync ──
    [Fact]
    public async Task ChangePasswordAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        var request = new ChangePasswordRequest("OldPass123!", "NewPass456!");
        TestAuthController sut = CreateController();

        ActionResult result = await sut.ChangePasswordAsync(request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task ChangePasswordAsync_Success_ReturnsNoContent()
    {
        const int userId = 42;
        var request = new ChangePasswordRequest("OldPass123!", "NewPass456!");
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock.Setup(x => x.ChangePasswordAsync(userId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestAuthController sut = CreateController();

        ActionResult result = await sut.ChangePasswordAsync(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ChangePasswordAsync_Failure_ReturnsHandleFailure()
    {
        const int userId = 42;
        var request = new ChangePasswordRequest("WrongPass!", "NewPass456!");
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock.Setup(x => x.ChangePasswordAsync(userId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Unauthorized("Auth.InvalidPassword", "Current password is incorrect")));
        TestAuthController sut = CreateController();

        ActionResult result = await sut.ChangePasswordAsync(request, CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }
}

internal sealed class TestAuthController(
    IAuthenticationService authenticationService,
    ICurrentUserService currentUserService) : AuthControllerBase(authenticationService, currentUserService);
