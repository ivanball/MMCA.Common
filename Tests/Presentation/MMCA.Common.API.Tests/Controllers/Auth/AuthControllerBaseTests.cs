using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Auth;

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
        _authServiceMock.Setup(x => x.LoginAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.LoginAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.RegisterAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.RegisterAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.RefreshTokenAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.RefreshTokenAsync(request, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.RevokeAllSessionsAsync(userId, It.IsAny<CancellationToken>()))
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
        _authServiceMock.Setup(x => x.RevokeAllSessionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFoundError("Auth.UserNotFound", "User not found")));
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeAsync(CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── GetMySessionsAsync ──
    [Fact]
    public async Task GetMySessionsAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        TestAuthController sut = CreateController();

        ActionResult<IReadOnlyList<RefreshSessionSummaryResponse>> result =
            await sut.GetMySessionsAsync(CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task GetMySessionsAsync_Success_ReturnsOkWithTheSessions()
    {
        const int userId = 42;
        var sessionId = Guid.NewGuid();
        IReadOnlyList<RefreshSessionSummaryResponse> sessions =
            [new RefreshSessionSummaryResponse(sessionId, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), "203.0.113.7", "Firefox", true)];
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock
            .Setup(x => x.GetSessionsAsync(userId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(sessions));
        TestAuthController sut = CreateController();

        ActionResult<IReadOnlyList<RefreshSessionSummaryResponse>> result =
            await sut.GetMySessionsAsync(CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().BeSameAs(sessions);
    }

    [Fact]
    public async Task GetMySessionsAsync_PassesTheCallersSidClaimThrough()
    {
        const int userId = 42;
        var sessionId = Guid.NewGuid();
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock
            .Setup(x => x.GetSessionsAsync(userId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RefreshSessionSummaryResponse>>([]));
        TestAuthController sut = CreateController();
        sut.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthClaimTypes.SessionId, sessionId.ToString("D"))],
            authenticationType: "Test"));

        await sut.GetMySessionsAsync(CancellationToken.None);

        _authServiceMock.Verify(
            x => x.GetSessionsAsync(userId, sessionId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the current-device marker comes off the caller's own token, not from client state");
    }

    [Fact]
    public async Task GetMySessionsAsync_WithNoSidClaim_PassesNull()
    {
        const int userId = 42;
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock
            .Setup(x => x.GetSessionsAsync(userId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RefreshSessionSummaryResponse>>([]));
        TestAuthController sut = CreateController();

        await sut.GetMySessionsAsync(CancellationToken.None);

        _authServiceMock.Verify(
            x => x.GetSessionsAsync(userId, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── RevokeSessionAsync ──
    [Fact]
    public async Task RevokeSessionAsync_WhenUserIdNull_ReturnsUnauthorized()
    {
        _currentUserServiceMock.Setup(x => x.UserId).Returns((int?)null);
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeSessionAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task RevokeSessionAsync_Success_ReturnsNoContent()
    {
        const int userId = 42;
        var sessionId = Guid.NewGuid();
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock
            .Setup(x => x.RevokeSessionByIdAsync(userId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeSessionAsync(sessionId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RevokeSessionAsync_NotFound_Returns404ProblemDetails()
    {
        const int userId = 42;
        var sessionId = Guid.NewGuid();
        _currentUserServiceMock.Setup(x => x.UserId).Returns(userId);
        _authServiceMock
            .Setup(x => x.RevokeSessionByIdAsync(userId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFoundError("Auth.SessionNotFound", "The session was not found.")));
        TestAuthController sut = CreateController();

        ActionResult result = await sut.RevokeSessionAsync(sessionId, CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── Route + authorization contract ──
    [Theory]
    [InlineData(nameof(AuthControllerBase.GetMySessionsAsync), "my-sessions")]
    [InlineData(nameof(AuthControllerBase.RevokeSessionAsync), "revoke/{sessionId:guid}")]
    public void SessionActions_CarryTheirDocumentedRoute(string actionName, string expectedTemplate)
    {
        MethodInfo action = typeof(AuthControllerBase).GetMethod(actionName)!;

        action.GetCustomAttributes<HttpMethodAttribute>().Single().Template.Should().Be(expectedTemplate);
    }

    [Theory]
    [InlineData(nameof(AuthControllerBase.GetMySessionsAsync))]
    [InlineData(nameof(AuthControllerBase.RevokeSessionAsync))]
    public void SessionActions_RequireAuthentication(string actionName)
    {
        MethodInfo action = typeof(AuthControllerBase).GetMethod(actionName)!;

        action.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(
            "a device list and a per-device sign-out are both about the caller's own account");
        action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }

    [Fact]
    public void RevokeSessionAsync_DeclaresItselfNonIdempotent()
    {
        MethodInfo action = typeof(AuthControllerBase).GetMethod(nameof(AuthControllerBase.RevokeSessionAsync))!;

        action.GetCustomAttribute<NonIdempotentAttribute>().Should().NotBeNull(
            "a replayed 204 would report success for a revoke that never reached the store");
    }
}

internal sealed class TestAuthController(
    IAuthenticationService authenticationService,
    ICurrentUserService currentUserService) : AuthControllerBase(authenticationService, currentUserService);
