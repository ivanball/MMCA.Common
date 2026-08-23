using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// Covers the password-recovery endpoints and pins their attributes. The attributes are the whole
/// protection here: both actions are anonymous by necessity, so losing the per-IP policy or the
/// idempotency marker would not break anything visible, it would just leave an unauthenticated
/// endpoint unthrottled.
/// </summary>
public sealed class PasswordResetAuthControllerBaseTests
{
    private readonly Mock<ICommandHandler<TestForgotPasswordCommand, Result>> _forgotHandlerMock = new();
    private readonly Mock<ICommandHandler<TestResetPasswordCommand, Result>> _resetHandlerMock = new();

    private TestPasswordResetController CreateController() =>
        new(_forgotHandlerMock.Object, _resetHandlerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    // ── ForgotPasswordAsync ──
    [Fact]
    public async Task ForgotPasswordAsync_Success_ReturnsAcceptedAndDispatchesAppCommand()
    {
        var request = new ForgotPasswordRequest("test@example.com");
        _forgotHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestForgotPasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestPasswordResetController sut = CreateController();

        ActionResult result = await sut.ForgotPasswordAsync(request, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        _forgotHandlerMock.Verify(
            x => x.HandleAsync(
                It.Is<TestForgotPasswordCommand>(c => c.Request == request),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ForgotPasswordAsync_Failure_ReturnsHandleFailure()
    {
        _forgotHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestForgotPasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation("Auth.InvalidEmail", "Email is required.")));
        TestPasswordResetController sut = CreateController();

        ActionResult result = await sut.ForgotPasswordAsync(
            new ForgotPasswordRequest(string.Empty),
            CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── ResetPasswordAsync ──
    [Fact]
    public async Task ResetPasswordAsync_Success_ReturnsNoContentAndDispatchesAppCommand()
    {
        var request = new ResetPasswordRequest("test@example.com", "token", "New-Password1!");
        _resetHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestResetPasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestPasswordResetController sut = CreateController();

        ActionResult result = await sut.ResetPasswordAsync(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _resetHandlerMock.Verify(
            x => x.HandleAsync(
                It.Is<TestResetPasswordCommand>(c => c.Request == request),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResetPasswordAsync_Failure_ReturnsHandleFailure()
    {
        _resetHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestResetPasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Unauthorized("Auth.InvalidResetToken", "The reset link is invalid or has expired.")));
        TestPasswordResetController sut = CreateController();

        ActionResult result = await sut.ResetPasswordAsync(
            new ResetPasswordRequest("test@example.com", "stale", "New-Password1!"),
            CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── Attribute pins ──
    [Theory]
    [InlineData(nameof(PasswordResetAuthControllerBase<,>.ForgotPasswordAsync))]
    [InlineData(nameof(PasswordResetAuthControllerBase<,>.ResetPasswordAsync))]
    public void RecoveryEndpoint_IsAnonymous(string actionName) =>
        Action(actionName).GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull(
                because: $"{actionName} serves a caller who has lost the credential, so requiring one would be circular");

    [Theory]
    [InlineData(nameof(PasswordResetAuthControllerBase<,>.ForgotPasswordAsync))]
    [InlineData(nameof(PasswordResetAuthControllerBase<,>.ResetPasswordAsync))]
    public void RecoveryEndpoint_CarriesTheAuthIpPolicy(string actionName)
    {
        var attribute = Action(actionName).GetCustomAttribute<EnableRateLimitingAttribute>();

        attribute.Should().NotBeNull(
            because: $"{actionName} is anonymous, so it must be throttled per client IP by default");
        attribute!.PolicyName.Should().Be(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp);
        attribute.PolicyName.Should().Be("auth-ip");
    }

    [Theory]
    [InlineData(nameof(PasswordResetAuthControllerBase<,>.ForgotPasswordAsync))]
    [InlineData(nameof(PasswordResetAuthControllerBase<,>.ResetPasswordAsync))]
    public void RecoveryEndpoint_IsIdempotent(string actionName) =>
        Action(actionName).GetCustomAttribute<IdempotentAttribute>()
            .Should().NotBeNull(
                because: $"{actionName} is safe to replay, and a retrying client must not mint a second token or a second 4xx");

    private static MethodInfo Action(string name) =>
        typeof(PasswordResetAuthControllerBase<TestForgotPasswordCommand, TestResetPasswordCommand>)
            .GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"PasswordResetAuthControllerBase.{name} not found; this guard must follow the base's action names.");
}

/// <summary>
/// Stand-in for an app forgot-password command (the real ones stay app-side). Public because Moq
/// proxies <c>ICommandHandler&lt;TestForgotPasswordCommand, Result&gt;</c> over it.
/// </summary>
public sealed record TestForgotPasswordCommand(ForgotPasswordRequest Request)
    : ICommandWithRequest<ForgotPasswordRequest>;

/// <summary>
/// Stand-in for an app reset-password command (the real ones stay app-side). Public because Moq
/// proxies <c>ICommandHandler&lt;TestResetPasswordCommand, Result&gt;</c> over it.
/// </summary>
public sealed record TestResetPasswordCommand(ResetPasswordRequest Request)
    : ICommandWithRequest<ResetPasswordRequest>;

internal sealed class TestPasswordResetController(
    ICommandHandler<TestForgotPasswordCommand, Result> forgotPasswordHandler,
    ICommandHandler<TestResetPasswordCommand, Result> resetPasswordHandler)
    : PasswordResetAuthControllerBase<TestForgotPasswordCommand, TestResetPasswordCommand>(
        forgotPasswordHandler,
        resetPasswordHandler)
{
    protected override TestForgotPasswordCommand CreateForgotPasswordCommand(ForgotPasswordRequest request) =>
        new(request);

    protected override TestResetPasswordCommand CreateResetPasswordCommand(ResetPasswordRequest request) =>
        new(request);
}
