using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Auth;

/// <summary>
/// Covers the email-confirmation endpoints and pins their attributes and routes. The attributes are
/// the whole protection here: both actions are anonymous by necessity, so losing the per-IP policy or
/// the idempotency marker would not break anything visible, it would just leave an unauthenticated
/// endpoint unthrottled. The routes are the ones <c>EmailConfirmationUIService</c> calls.
/// </summary>
public sealed class EmailConfirmationControllerBaseTests
{
    private readonly Mock<ICommandHandler<TestSendEmailConfirmationCommand, Result>> _sendHandlerMock = new();
    private readonly Mock<ICommandHandler<TestConfirmEmailCommand, Result>> _confirmHandlerMock = new();

    private TestEmailConfirmationController CreateController() =>
        new(_sendHandlerMock.Object, _confirmHandlerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    // ── SendEmailConfirmationAsync ──
    [Fact]
    public async Task SendEmailConfirmationAsync_Success_ReturnsAcceptedAndDispatchesAppCommand()
    {
        var request = new SendEmailConfirmationRequest("test@example.com");
        _sendHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestSendEmailConfirmationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestEmailConfirmationController sut = CreateController();

        ActionResult result = await sut.SendEmailConfirmationAsync(request, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        _sendHandlerMock.Verify(
            x => x.HandleAsync(
                It.Is<TestSendEmailConfirmationCommand>(c => c.Request == request),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendEmailConfirmationAsync_Failure_ReturnsProblemDetails()
    {
        _sendHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestSendEmailConfirmationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation("Auth.InvalidEmail", "Email is required.")));
        TestEmailConfirmationController sut = CreateController();

        ActionResult result = await sut.SendEmailConfirmationAsync(
            new SendEmailConfirmationRequest(string.Empty),
            CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── ConfirmEmailAsync ──
    [Fact]
    public async Task ConfirmEmailAsync_Success_ReturnsNoContentAndDispatchesAppCommand()
    {
        var request = new ConfirmEmailRequest("test@example.com", "token");
        _confirmHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestConfirmEmailCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestEmailConfirmationController sut = CreateController();

        ActionResult result = await sut.ConfirmEmailAsync(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _confirmHandlerMock.Verify(
            x => x.HandleAsync(
                It.Is<TestConfirmEmailCommand>(c => c.Request == request),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmEmailAsync_Failure_ReturnsProblemDetails()
    {
        _confirmHandlerMock
            .Setup(x => x.HandleAsync(It.IsAny<TestConfirmEmailCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.Unauthorized("Auth.InvalidConfirmationToken", "The confirmation link is invalid or has expired.")));
        TestEmailConfirmationController sut = CreateController();

        ActionResult result = await sut.ConfirmEmailAsync(
            new ConfirmEmailRequest("test@example.com", "stale"),
            CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── Attribute pins ──
    [Theory]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.SendEmailConfirmationAsync))]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.ConfirmEmailAsync))]
    public void ConfirmationEndpoint_IsAnonymous(string actionName) =>
        Action(actionName).GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull(
                because: $"{actionName} is reached from a mail client with no session, so requiring one would lock out the user it serves");

    [Theory]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.SendEmailConfirmationAsync))]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.ConfirmEmailAsync))]
    public void ConfirmationEndpoint_CarriesTheAuthIpPolicy(string actionName)
    {
        var attribute = Action(actionName).GetCustomAttribute<EnableRateLimitingAttribute>();

        attribute.Should().NotBeNull(
            because: $"{actionName} is anonymous, so it must be throttled per client IP by default");
        attribute!.PolicyName.Should().Be(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp);
        attribute.PolicyName.Should().Be("auth-ip");
    }

    [Theory]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.SendEmailConfirmationAsync))]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.ConfirmEmailAsync))]
    public void ConfirmationEndpoint_IsIdempotent(string actionName) =>
        Action(actionName).GetCustomAttribute<IdempotentAttribute>()
            .Should().NotBeNull(
                because: $"{actionName} is safe to replay, and a retrying client must not mail a second link or burn the token twice");

    [Theory]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.SendEmailConfirmationAsync), "send-email-confirmation")]
    [InlineData(nameof(EmailConfirmationControllerBase<,>.ConfirmEmailAsync), "confirm-email")]
    public void ConfirmationEndpoint_KeepsTheRouteTheUiClientCalls(string actionName, string template) =>
        Action(actionName).GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be(
            template,
            "EmailConfirmationUIService posts to Auth/{0}, so renaming the action route breaks the confirmation page",
            template);

    private static MethodInfo Action(string name) =>
        typeof(EmailConfirmationControllerBase<TestSendEmailConfirmationCommand, TestConfirmEmailCommand>)
            .GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"EmailConfirmationControllerBase.{name} not found; this guard must follow the base's action names.");
}

/// <summary>
/// Stand-in for an app send-email-confirmation command (the real ones stay app-side). Public because
/// Moq proxies <c>ICommandHandler&lt;TestSendEmailConfirmationCommand, Result&gt;</c> over it.
/// </summary>
public sealed record TestSendEmailConfirmationCommand(SendEmailConfirmationRequest Request)
    : ICommandWithRequest<SendEmailConfirmationRequest>;

/// <summary>
/// Stand-in for an app confirm-email command (the real ones stay app-side). Public because Moq
/// proxies <c>ICommandHandler&lt;TestConfirmEmailCommand, Result&gt;</c> over it.
/// </summary>
public sealed record TestConfirmEmailCommand(ConfirmEmailRequest Request)
    : ICommandWithRequest<ConfirmEmailRequest>;

internal sealed class TestEmailConfirmationController(
    ICommandHandler<TestSendEmailConfirmationCommand, Result> sendHandler,
    ICommandHandler<TestConfirmEmailCommand, Result> confirmHandler)
    : EmailConfirmationControllerBase<TestSendEmailConfirmationCommand, TestConfirmEmailCommand>(
        sendHandler,
        confirmHandler)
{
    protected override TestSendEmailConfirmationCommand CreateSendCommand(SendEmailConfirmationRequest request) =>
        new(request);

    protected override TestConfirmEmailCommand CreateConfirmCommand(ConfirmEmailRequest request) =>
        new(request);
}
