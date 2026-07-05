using System.IdentityModel.Tokens.Jwt;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="WasmTokenStorageService"/> (cookie-only WASM token storage): the in-memory
/// access token is hydrated on demand through <see cref="ITokenRefresher"/>, a fresh in-memory JWT
/// short-circuits re-acquisition, near-expiry and non-JWT tokens refresh proactively, concurrent
/// callers share one single-flight acquisition, the refresh token never surfaces client-side, and
/// set/clear round-trip through <see cref="ISessionCookieSync"/>.
/// </summary>
public sealed class WasmTokenStorageServiceTests
{
    private sealed record Mocks(Mock<ISessionCookieSync> CookieSync, Mock<ITokenRefresher> Refresher);

    private static (WasmTokenStorageService Sut, Mocks Mocks) CreateSut(string? refresherToken = "hydrated-token")
    {
        var cookieSync = new Mock<ISessionCookieSync>();
        var refresher = new Mock<ITokenRefresher>();
        refresher
            .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(refresherToken);
        return (new WasmTokenStorageService(cookieSync.Object, refresher.Object), new Mocks(cookieSync, refresher));
    }

    private static string CreateJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    // == Hydration ==
    [Fact]
    public async Task GetAccessTokenAsync_WithNothingInMemory_HydratesViaRefresher()
    {
        var (sut, mocks) = CreateSut();

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("hydrated-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenRefresherReportsNoSession_ReturnsNull()
    {
        var (sut, _) = CreateSut(refresherToken: null);

        var result = await sut.GetAccessTokenAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithFreshStoredJwt_ReturnsItWithoutRefresher()
    {
        var (sut, mocks) = CreateSut();
        var freshJwt = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        await sut.SetTokensAsync(freshJwt, "refresh-token");

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be(freshJwt);
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithNonJwtStoredToken_HydratesViaRefresher()
    {
        var (sut, mocks) = CreateSut();
        await sut.SetTokensAsync("not-a-jwt", "refresh-token");

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("hydrated-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithJwtInsideExpirySkew_RefreshesProactively()
    {
        // Valid for 10 more seconds, but the 30-second skew treats it as expired.
        var (sut, mocks) = CreateSut();
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddSeconds(10)), "refresh-token");

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be("hydrated-token");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallers_ShareOneSingleFlightAcquisition()
    {
        var cookieSync = new Mock<ISessionCookieSync>();
        var refresher = new Mock<ITokenRefresher>();
        var pendingAcquisition = new TaskCompletionSource<string?>();
        refresher
            .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .Returns(pendingAcquisition.Task);
        var sut = new WasmTokenStorageService(cookieSync.Object, refresher.Object);

        var first = sut.GetAccessTokenAsync();
        var second = sut.GetAccessTokenAsync();
        pendingAcquisition.SetResult("hydrated-token");

        (await first).Should().Be("hydrated-token");
        (await second).Should().Be("hydrated-token");
        refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // == Refresh token never surfaces in the browser ==
    [Fact]
    public async Task GetRefreshTokenAsync_EvenAfterSet_ReturnsNull()
    {
        var (sut, _) = CreateSut();
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddMinutes(10)), "refresh-token");

        var result = await sut.GetRefreshTokenAsync();

        result.Should().BeNull();
    }

    // == Set / clear round-trip through the cookie sync ==
    [Fact]
    public async Task SetTokensAsync_SeedsHttpOnlyCookiesViaSessionSync()
    {
        var (sut, mocks) = CreateSut();

        await sut.SetTokensAsync("access-token", "refresh-token");

        mocks.CookieSync.Verify(c => c.SyncAsync("access-token", "refresh-token"), Times.Once);
    }

    [Fact]
    public async Task ClearTokensAsync_ClearsMemoryAndCookies()
    {
        var (sut, mocks) = CreateSut();
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddMinutes(10)), "refresh-token");

        await sut.ClearTokensAsync();
        var afterClear = await sut.GetAccessTokenAsync();

        mocks.CookieSync.Verify(c => c.ClearAsync(), Times.Once);
        afterClear.Should().Be("hydrated-token", "a cleared in-memory token must re-hydrate from the cookie session");
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
