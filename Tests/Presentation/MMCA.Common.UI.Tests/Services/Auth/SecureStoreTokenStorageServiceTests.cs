using System.IdentityModel.Tokens.Jwt;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth.Tokens;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="SecureStoreTokenStorageService"/> (the MAUI head's token storage over OS
/// SecureStorage). The case it was written for: a refresh answered with a DEFINITIVE "no session" for a
/// pair this device held must clear the pair and raise <see cref="ISessionAwareTokenStorage.SessionEnded"/>
/// once, instead of keeping the dead pair and re-posting a doomed refresh on every read (the
/// 2026-10-17 loop). A transient failure keeps the pair, and a sign-in that lands mid-refresh wins.
/// </summary>
public sealed class SecureStoreTokenStorageServiceTests
{
    private const string StoredRefresh = "stored-refresh";

    [Fact]
    public async Task GetAccessTokenAsync_WithAFreshStoredToken_ReturnsIt_WithoutRefreshing()
    {
        var fresh = CreateJwt(DateTime.UtcNow.AddMinutes(10));
        var store = new FakeSecureTokenStore(fresh, StoredRefresh);
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        using var sut = new SecureStoreTokenStorageService(store, refresher.Object);

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be(fresh);
        refresher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTheHeldSessionIsRefusedForGood_ClearsThePair_AndRaisesSessionEndedOnce()
    {
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = SessionAwareRefresher(TokenAcquisition.NoSession);
        using var sut = new SecureStoreTokenStorageService(store, refresher.Object);
        var ended = 0;
        sut.SessionEnded += (_, _) => ended++;

        var first = await sut.GetAccessTokenAsync();
        var second = await sut.GetAccessTokenAsync();

        first.Should().BeNull();
        second.Should().BeNull();
        store.AccessToken.Should().BeNull("the dead pair must not stay stored");
        store.RefreshToken.Should().BeNull();
        ended.Should().Be(1, "the shell signs out once; a read with no pair left is not a second ending");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTheRefreshIsUnavailable_KeepsThePair_AndStaysSignedIn()
    {
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = SessionAwareRefresher(TokenAcquisition.Unavailable);
        using var sut = new SecureStoreTokenStorageService(store, refresher.Object);
        var ended = 0;
        sut.SessionEnded += (_, _) => ended++;

        var result = await sut.GetAccessTokenAsync();

        result.Should().BeNull();
        store.RefreshToken.Should().Be(StoredRefresh, "a 409, a 5xx or a timeout says nothing about the session");
        ended.Should().Be(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTheRefreshSucceeds_ReturnsTheNewToken()
    {
        var renewed = CreateJwt(DateTime.UtcNow.AddMinutes(15));
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = SessionAwareRefresher(TokenAcquisition.Acquired(renewed));
        using var sut = new SecureStoreTokenStorageService(store, refresher.Object);

        var result = await sut.GetAccessTokenAsync();

        result.Should().Be(renewed);
        store.RefreshToken.Should().Be(StoredRefresh, "the refresher, not the storage, persists the rotated pair");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenASignInLandsMidRefresh_KeepsTheNewPair()
    {
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var pending = new TaskCompletionSource<TokenAcquisition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var sut = new SecureStoreTokenStorageService(store, refresher.Object);
        var ended = 0;
        sut.SessionEnded += (_, _) => ended++;

        var read = sut.GetAccessTokenAsync();
        var signedIn = CreateJwt(DateTime.UtcNow.AddHours(1));
        await sut.SetTokensAsync(signedIn, "new-refresh");
        pending.SetResult(TokenAcquisition.NoSession);
        await read;

        store.AccessToken.Should().Be(signedIn, "the answer was about the session before the sign-in");
        store.RefreshToken.Should().Be("new-refresh");
        ended.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_RemovesTheInstancesHandlers_FromTheProcessWideSignal()
    {
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = SessionAwareRefresher(TokenAcquisition.NoSession);
        var disposedListener = 0;
        var scope = new SecureStoreTokenStorageService(store, refresher.Object);
        scope.SessionEnded += (_, _) => disposedListener++;
        scope.Dispose();
        using var other = new SecureStoreTokenStorageService(store, refresher.Object);

        await other.GetAccessTokenAsync();

        disposedListener.Should().Be(0, "a disposed scope's subscriber must not be kept alive by the static event");
    }

    [Fact]
    public async Task SessionEnded_RaisedByOneInstance_ReachesTheListenerOnAnother()
    {
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = SessionAwareRefresher(TokenAcquisition.NoSession);
        using var componentScope = new SecureStoreTokenStorageService(store, refresher.Object);
        using var handlerScope = new SecureStoreTokenStorageService(store, refresher.Object);
        var ended = 0;
        componentScope.SessionEnded += (_, _) => ended++;

        await handlerScope.GetAccessTokenAsync();

        ended.Should().Be(1, "the delegating handler's instance hits the refusal while the auth state listens on the component's");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithAPlainRefresher_PassesItsNullThrough_AndLeavesThePair()
    {
        var store = new FakeSecureTokenStore(CreateJwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = new Mock<ITokenRefresher>();
        refresher.Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        using var sut = new SecureStoreTokenStorageService(store, refresher.Object);

        var result = await sut.GetAccessTokenAsync();

        result.Should().BeNull();
        store.RefreshToken.Should().Be(StoredRefresh, "a refresher that cannot tell no-session from unavailable proves nothing");
    }

    private static Mock<ISessionAwareTokenRefresher> SessionAwareRefresher(TokenAcquisition outcome)
    {
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        return refresher;
    }

    private static string CreateJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    private sealed class FakeSecureTokenStore(string? accessToken, string? refreshToken) : ISecureTokenStore
    {
        public string? AccessToken { get; private set; } = accessToken;

        public string? RefreshToken { get; private set; } = refreshToken;

        public Task<string?> GetAccessTokenAsync() => Task.FromResult(AccessToken);

        public Task<string?> GetRefreshTokenAsync() => Task.FromResult(RefreshToken);

        public Task SetTokensAsync(string accessToken, string refreshToken)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task ClearTokensAsync()
        {
            AccessToken = null;
            RefreshToken = null;
            return Task.CompletedTask;
        }
    }
}
