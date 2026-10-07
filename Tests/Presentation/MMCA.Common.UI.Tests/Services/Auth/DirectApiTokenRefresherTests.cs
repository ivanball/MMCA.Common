using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="DirectApiTokenRefresher"/> (the MAUI-host refresher): the stored pair is
/// exchanged in a single POST to <c>auth/refresh</c> on the named APIClient, the rotated pair is
/// persisted back to storage, and every failure path (missing tokens, endpoint rejection, empty
/// rotation payload) reports null without persisting anything or retrying.
/// </summary>
public sealed class DirectApiTokenRefresherTests
{
    private sealed record Mocks(
        StubHttpMessageHandler Handler,
        StubHttpClientFactory Factory,
        Mock<ISecureTokenStore> TokenStore);

    private static (DirectApiTokenRefresher Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? accessToken = "old-access",
        string? refreshToken = "old-refresh")
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStore = new Mock<ISecureTokenStore>();
        tokenStore.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(accessToken);
        tokenStore.Setup(s => s.GetRefreshTokenAsync()).ReturnsAsync(refreshToken);
        return (new DirectApiTokenRefresher(factory, tokenStore.Object), new Mocks(handler, factory, tokenStore));
    }

    private static HttpResponseMessage TokenResponse(string accessToken, string refreshToken) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new AuthenticationResponse(accessToken, refreshToken, DateTime.UtcNow.AddMinutes(15))),
        };

    /// <summary>The API's answer when the presented token was rotated by another request inside the reuse grace.</summary>
    private static HttpResponseMessage SupersededResponse() =>
        new(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{ "title": "Conflict", "status": 409, "errors": [ { "code": "Auth.RefreshSuperseded", "message": "The refresh token was already rotated by a concurrent request; retry with the current session.", "type": "Conflict" } ] }""",
                System.Text.Encoding.UTF8,
                "application/problem+json"),
        };

    // == Happy path ==
    [Fact]
    public async Task AcquireAccessTokenAsync_WithStoredPair_ExchangesAtAuthRefreshAndRotates()
    {
        var (sut, mocks) = CreateSut(_ => TokenResponse("new-access", "new-refresh"));

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().Be("new-access");
        mocks.Factory.LastClientName.Should().Be("APIClient");
        mocks.Handler.CallCount.Should().Be(1);
        mocks.Handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        mocks.Handler.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/refresh");
        mocks.Handler.LastRequest.Body.Should().Contain("old-access").And.Contain("old-refresh");
        mocks.TokenStore.Verify(s => s.SetTokensAsync("new-access", "new-refresh"), Times.Once);
    }

    [Fact]
    public async Task AcquireAccessTokenAsync_WithStoredPair_MarksTheRefreshPostToSkipTheBearer()
    {
        // H37: the refresh POST rides the APIClient pipeline; without the opt-out the delegating
        // handler reads the storage that is awaiting this very refresh.
        var skipBearer = false;
        var (sut, _) = CreateSut(request =>
        {
            skipBearer = request.Options.TryGetValue(AuthDelegatingHandler.SkipBearer, out var skip) && skip;
            return TokenResponse("new-access", "new-refresh");
        });

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().Be("new-access");
        skipBearer.Should().BeTrue();
    }

    // == Missing credentials: no HTTP round-trip at all ==
    [Theory]
    [InlineData(null, "old-refresh")]
    [InlineData("  ", "old-refresh")]
    [InlineData("old-access", null)]
    [InlineData("old-access", "  ")]
    public async Task AcquireAccessTokenAsync_WithMissingStoredToken_ReturnsNullWithoutHttpCall(
        string? accessToken, string? refreshToken)
    {
        var (sut, mocks) = CreateSut(
            _ => TokenResponse("new-access", "new-refresh"), accessToken, refreshToken);

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
        mocks.Handler.CallCount.Should().Be(0);
        mocks.TokenStore.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // == Failure paths ==
    [Fact]
    public async Task AcquireAccessTokenAsync_WhenRefreshEndpointRejects_ReturnsNullAndPersistsNothing()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
        mocks.TokenStore.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AcquireAccessTokenAsync_WhenRotationPayloadHasEmptyAccessToken_ReturnsNull()
    {
        var (sut, mocks) = CreateSut(_ => TokenResponse(string.Empty, "new-refresh"));

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
        mocks.TokenStore.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // == 409 refresh superseded: transient, never a sign-out ==
    // Someone else rotated this client's refresh token inside the server's reuse grace. If the client
    // dropped its token here it would never present it again, and the server's reuse detection
    // (BR-206) would never revoke the other holder's family. So the stored pair is kept, the outcome
    // is transient, and the next attempt presents the same token again.
    [Fact]
    public async Task TryAcquireAccessTokenAsync_WhenTheRefreshIsSuperseded_IsTransient_KeepsTheStoredPair_AndPresentsItAgain()
    {
        var (sut, mocks) = CreateSut(_ => SupersededResponse());
        ITokenRefresher refresher = sut;
        var sessionAware = refresher.Should().BeAssignableTo<ISessionAwareTokenRefresher>().Subject;

        var acquisition = await sessionAware.TryAcquireAccessTokenAsync(TestContext.Current.CancellationToken);
        await sessionAware.TryAcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        acquisition.IsUnavailable.Should().BeTrue("a 409 says nothing about whether this client's session is gone");
        acquisition.AccessToken.Should().BeNull();
        mocks.TokenStore.Verify(s => s.ClearTokensAsync(), Times.Never);
        mocks.TokenStore.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mocks.Handler.CallCount.Should().Be(2);
        mocks.Handler.LastRequest.Body.Should().Contain("old-refresh", "the next attempt presents the same refresh token again");
    }

    [Fact]
    public async Task TryAcquireAccessTokenAsync_WhenTheRefreshIsSuperseded_ByANewerPairAlreadyStored_UsesTheStoredPair()
    {
        // Another refresh in this process won the race and stored its rotated pair while this one
        // was in flight: the store is re-read and the newer pair is the answer.
        Mocks? captured = null;
        var (sut, mocks) = CreateSut(_ =>
        {
            captured!.TokenStore.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("winner-access");
            captured.TokenStore.Setup(s => s.GetRefreshTokenAsync()).ReturnsAsync("winner-refresh");
            return SupersededResponse();
        });
        captured = mocks;
        ITokenRefresher refresher = sut;
        var sessionAware = refresher.Should().BeAssignableTo<ISessionAwareTokenRefresher>().Subject;

        var acquisition = await sessionAware.TryAcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        acquisition.AccessToken.Should().Be("winner-access");
        mocks.TokenStore.Verify(s => s.ClearTokensAsync(), Times.Never);
        mocks.TokenStore.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TryAcquireAccessTokenAsync_WhenTheRefreshEndpointRejects_ReportsNoSession()
    {
        var (sut, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        ITokenRefresher refresher = sut;
        var sessionAware = refresher.Should().BeAssignableTo<ISessionAwareTokenRefresher>().Subject;

        var acquisition = await sessionAware.TryAcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        acquisition.IsUnavailable.Should().BeFalse("a 401 is the definitive answer that the session is gone");
        acquisition.AccessToken.Should().BeNull();
    }

    [Fact]
    public async Task AcquireAccessTokenAsync_OnRepeatedFailure_MakesExactlyOneCallPerInvocation()
    {
        // Pins the no-retry contract: a failed refresh must not loop internally; the caller decides
        // whether to try again.
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);
        await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        mocks.Handler.CallCount.Should().Be(2);
    }
}
