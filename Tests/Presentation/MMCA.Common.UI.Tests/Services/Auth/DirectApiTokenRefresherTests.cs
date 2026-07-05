#pragma warning disable CA2000 // Dispose objects before losing scope - test doubles do not hold real resources

using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Services.Auth;
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
        Mock<ITokenStorageService> TokenStorage);

    private static (DirectApiTokenRefresher Sut, Mocks Mocks) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? accessToken = "old-access",
        string? refreshToken = "old-refresh")
    {
        var handler = new StubHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(accessToken);
        tokenStorage.Setup(s => s.GetRefreshTokenAsync()).ReturnsAsync(refreshToken);
        return (new DirectApiTokenRefresher(factory, tokenStorage.Object), new Mocks(handler, factory, tokenStorage));
    }

    private static HttpResponseMessage TokenResponse(string accessToken, string refreshToken) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new AuthenticationResponse(accessToken, refreshToken, DateTime.UtcNow.AddMinutes(15))),
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
        mocks.TokenStorage.Verify(s => s.SetTokensAsync("new-access", "new-refresh"), Times.Once);
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
        mocks.TokenStorage.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // == Failure paths ==
    [Fact]
    public async Task AcquireAccessTokenAsync_WhenRefreshEndpointRejects_ReturnsNullAndPersistsNothing()
    {
        var (sut, mocks) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
        mocks.TokenStorage.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AcquireAccessTokenAsync_WhenRotationPayloadHasEmptyAccessToken_ReturnsNull()
    {
        var (sut, mocks) = CreateSut(_ => TokenResponse(string.Empty, "new-refresh"));

        var result = await sut.AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
        mocks.TokenStorage.Verify(s => s.SetTokensAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
