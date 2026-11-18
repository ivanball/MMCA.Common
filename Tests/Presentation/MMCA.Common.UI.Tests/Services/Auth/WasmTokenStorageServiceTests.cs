using AwesomeAssertions;
using MMCA.Common.Tests.Shared.UI;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="WasmTokenStorageService"/> (cookie-only WASM token storage): the in-memory
/// contract it shares with the Blazor Server circuit storage runs from
/// <see cref="TokenStorageServiceTestsBase"/>; the cases below are WASM-only (a null hydrate, a
/// non-JWT stored token, the grace a set ends, a session-aware refresher's definitive answer, and a
/// transient failure followed by no session).
/// </summary>
public sealed class WasmTokenStorageServiceTests : TokenStorageServiceTestsBase
{
    // == Hydration ==
    [Fact]
    public async Task GetAccessTokenAsync_WhenRefresherReportsNoSession_ReturnsNull()
    {
        var (sut, _) = CreateSut(refresherToken: null);

        var result = await sut.GetAccessTokenAsync();

        result.Should().BeNull();
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

    // == L132: the anonymous grace ==
    [Fact]
    public async Task SetTokensAsync_AfterANegativeAnswer_EndsTheGrace()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (sut, mocks) = CreateSut(refresherToken: null, timeProvider: clock);

        (await sut.GetAccessTokenAsync()).Should().BeNull();
        await sut.SetTokensAsync("not-a-jwt", "refresh-token");
        await sut.GetAccessTokenAsync();

        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterADefinitiveNoSession_FromASessionAwareRefresher_RemembersIt()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.SetupSequence(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenAcquisition.NoSession)
            .ReturnsAsync(TokenAcquisition.Acquired("hydrated-token"));
        var sut = new WasmTokenStorageService(Mock.Of<ISessionCookieSync>(), refresher.Object, clock);

        (await sut.GetAccessTokenAsync()).Should().BeNull();
        (await sut.GetAccessTokenAsync()).Should().BeNull("a definitive no-session answer is remembered for the grace");

        refresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // == A-34: a held session that ended remotely signs the shell out ==
    [Fact]
    public async Task GetAccessTokenAsync_WhenTheRefreshFailsTransientlyThenSaysNoSession_StillNotifiesLogout()
    {
        // The transient failure drops the stale token from memory, but the client still held a
        // session, so the definitive answer that follows is still a sign-out.
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.SetupSequence(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenAcquisition.Unavailable)
            .ReturnsAsync(TokenAcquisition.NoSession);
        var (sut, provider, _) = await CreateSignedInSutAsync(refresher);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;

        (await sut.GetAccessTokenAsync()).Should().BeNull();
        (await sut.GetAccessTokenAsync()).Should().BeNull();

        notifications.Should().Be(1);
    }

    protected override ISessionAwareTokenStorage CreateStorage(
        ISessionCookieSync sessionCookieSync,
        ITokenRefresher tokenRefresher,
        TimeProvider? timeProvider) => new WasmTokenStorageService(sessionCookieSync, tokenRefresher, timeProvider);
}
