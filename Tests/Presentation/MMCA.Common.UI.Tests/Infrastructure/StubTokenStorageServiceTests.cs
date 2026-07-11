using AwesomeAssertions;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="StubTokenStorageService"/> (Testing.UI): the canned defaults, the
/// login/logout mutations (<c>SetTokensAsync</c>/<c>ClearTokensAsync</c>), the default
/// <see cref="StubTokenStorageService.AccessTokenProvider"/> tracking the mutable
/// <see cref="StubTokenStorageService.AccessToken"/>, and the swappable throwing delegate used to
/// simulate the prerender window where platform storage is unreachable.
/// </summary>
public sealed class StubTokenStorageServiceTests
{
    // ── Canned defaults ──
    [Fact]
    public async Task Defaults_ReturnTheCannedAccessAndRefreshTokens()
    {
        var sut = new StubTokenStorageService();

        (await sut.GetAccessTokenAsync()).Should().Be("test-token");
        (await sut.GetRefreshTokenAsync()).Should().Be("test-refresh-token");
    }

    [Fact]
    public async Task NullAccessToken_ProducesAnAnonymousStub()
    {
        var sut = new StubTokenStorageService(accessToken: null, refreshToken: null);

        (await sut.GetAccessTokenAsync()).Should().BeNull();
        (await sut.GetRefreshTokenAsync()).Should().BeNull();
    }

    // ── Login/logout mutations ──
    [Fact]
    public async Task SetTokensAsync_UpdatesBothCannedValues()
    {
        var sut = new StubTokenStorageService();

        await sut.SetTokensAsync("new-access", "new-refresh");

        sut.AccessToken.Should().Be("new-access");
        sut.RefreshToken.Should().Be("new-refresh");
        (await sut.GetAccessTokenAsync()).Should().Be("new-access");
        (await sut.GetRefreshTokenAsync()).Should().Be("new-refresh");
    }

    [Fact]
    public async Task ClearTokensAsync_NullsBothCannedValues()
    {
        var sut = new StubTokenStorageService();

        await sut.ClearTokensAsync();

        sut.AccessToken.Should().BeNull();
        sut.RefreshToken.Should().BeNull();
        (await sut.GetAccessTokenAsync()).Should().BeNull();
        (await sut.GetRefreshTokenAsync()).Should().BeNull();
    }

    // ── The swappable provider ──
    [Fact]
    public async Task DefaultProvider_TracksTheMutableAccessTokenProperty()
    {
        var sut = new StubTokenStorageService { AccessToken = "rotated-token" };

        (await sut.GetAccessTokenAsync()).Should().Be(
            "rotated-token", "the default provider reads AccessToken at call time, not construction time");
    }

    [Fact]
    public async Task ThrowingProvider_SurfacesTheFailureFromGetAccessTokenAsync()
    {
        // Simulates the SSR prerender window: JS interop (and so platform token storage) is
        // unavailable, which services must tolerate without crashing the render.
        var sut = new StubTokenStorageService
        {
            AccessTokenProvider = () => throw new InvalidOperationException("JS interop unavailable during prerender"),
        };

        Func<Task> act = sut.GetAccessTokenAsync;

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*prerender*");
    }
}
