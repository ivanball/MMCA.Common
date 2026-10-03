using System.Net;
using AwesomeAssertions;
using MMCA.Common.Shared.Legal;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Legal;

namespace MMCA.Common.UI.Tests.Services.Legal;

/// <summary>
/// Tests for <see cref="LegalAcceptanceUIService"/>: the read is a bearer-authenticated GET and the
/// acceptance a POST carrying the version, both to <c>Users/me/legal-acceptance</c>, and a
/// non-success answer comes back as a failed result rather than an exception (the gate treats any
/// failure as "do not block").
/// </summary>
public sealed class LegalAcceptanceUIServiceTests
{
    private const string Endpoint = "/Users/me/legal-acceptance";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_SendsAnAuthenticatedGetAndReadsTheStanding()
    {
        var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(
            HttpMethod.Get,
            Endpoint,
            HttpStatusCode.OK,
            LegalAcceptanceDTO.Evaluate("v2", "v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var sut = CreateSut(handler);

        var result = await sut.GetAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentVersion.Should().Be("v2");
        result.Value.AcceptedVersion.Should().Be("v1");
        result.Value.IsCurrent.Should().BeFalse();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Path.Should().Be(Endpoint);
        request.Authorization.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task GetAsync_OnANonSuccessAnswer_ReturnsAFailureWithoutThrowing()
    {
        // Unregistered route: the handler answers 404, which is what a host serving no such endpoint sends.
        var handler = new CapturingHttpMessageHandler();
        var sut = CreateSut(handler);

        var act = async () => await sut.GetAsync(Ct);

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptAsync_PostsTheVersionToTheEndpoint()
    {
        var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(
            HttpMethod.Post,
            Endpoint,
            HttpStatusCode.OK,
            LegalAcceptanceDTO.Evaluate("v2", "v2", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var sut = CreateSut(handler);

        var result = await sut.AcceptAsync("v2", Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsCurrent.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be(Endpoint);
        request.Authorization.Should().Be("Bearer test-token");
        request.Body.Should().Contain("\"version\"").And.Contain("\"v2\"");
    }

    [Fact]
    public async Task AcceptAsync_OnARefusal_ReturnsTheApisErrorWithoutThrowing()
    {
        var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(
            HttpMethod.Post,
            Endpoint,
            HttpStatusCode.BadRequest,
            new
            {
                title = "Operation failed",
                status = 400,
                errors = new[]
                {
                    new { code = LegalAcceptanceErrorCodes.VersionNotCurrent, message = "Not current.", type = "Validation" },
                },
            });
        var sut = CreateSut(handler);

        var act = async () => await sut.AcceptAsync("v1", Ct);

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsFailure.Should().BeTrue();
        result.Subject.Errors.Should().Contain(e => e.Code == LegalAcceptanceErrorCodes.VersionNotCurrent);
        handler.Requests.Should().ContainSingle("the acceptance is sent once, never retried");
    }

    private static LegalAcceptanceUIService CreateSut(CapturingHttpMessageHandler handler) =>
        new(HttpTestDoubles.ClientFactory(handler), HttpTestDoubles.TokenStorage());
}
