using System.Net;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Tests for <see cref="EmailConfirmationUIService"/>: each call posts exactly once to its anonymous
/// <c>auth/*</c> endpoint (the token is single-use, so a transient failure is reported, never
/// retried), and a refusal comes back as the API's own error.
/// </summary>
public sealed class EmailConfirmationUIServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConfirmEmailAsync_PostsTheEmailAndTokenOnce()
    {
        var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Post, "/auth/confirm-email", HttpStatusCode.NoContent);
        var sut = new EmailConfirmationUIService(HttpTestDoubles.ClientFactory(handler));

        var result = await sut.ConfirmEmailAsync("ada@example.com", "tok-123", Ct);

        result.IsSuccess.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/auth/confirm-email");
        request.Body.Should().Contain("ada@example.com").And.Contain("tok-123");
    }

    [Fact]
    public async Task ConfirmEmailAsync_OnAServerError_ReportsItWithoutRetrying()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var sut = new EmailConfirmationUIService(HttpTestDoubles.ClientFactory(handler));

        var result = await sut.ConfirmEmailAsync("ada@example.com", "tok-123", Ct);

        result.IsFailure.Should().BeTrue();
        handler.Requests.Should().ContainSingle("a retried POST would try to spend the single-use token twice");
    }

    [Fact]
    public async Task ConfirmEmailAsync_OnARefusal_ReturnsTheApisError()
    {
        var handler = new CapturingHttpMessageHandler(_ => HttpTestDoubles.ProblemResponse(
            "Invalid confirmation token.", statusCode: HttpStatusCode.Unauthorized));
        var sut = new EmailConfirmationUIService(HttpTestDoubles.ClientFactory(handler));

        var result = await sut.ConfirmEmailAsync("ada@example.com", "stale", Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task ResendEmailConfirmationAsync_PostsTheAddressOnceAndTreats202AsSuccess()
    {
        var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Post, "/auth/send-email-confirmation", HttpStatusCode.Accepted);
        var sut = new EmailConfirmationUIService(HttpTestDoubles.ClientFactory(handler));

        var result = await sut.ResendEmailConfirmationAsync("ada@example.com", Ct);

        result.IsSuccess.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/auth/send-email-confirmation");
        request.Body.Should().Contain("ada@example.com");
    }
}
