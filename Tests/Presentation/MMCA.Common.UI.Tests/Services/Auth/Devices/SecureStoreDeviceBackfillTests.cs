using System.IdentityModel.Tokens.Jwt;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth.Devices;
using MMCA.Common.UI.Services.Auth.Tokens;
using Moq;
using TestContext = Xunit.TestContext;

namespace MMCA.Common.UI.Tests.Services.Auth.Devices;

/// <summary>
/// The device backfill on the native heads' refresh path (<see cref="SecureStoreTokenStorageService"/>):
/// after a successful refresh the device is reported for the session the new token names, until one
/// report in the process succeeds. A failed refresh reports nothing, a failed report is retried on the
/// next refresh, and once a report succeeded later refreshes stay silent.
/// </summary>
[Collection(SecureStoreTokenStorageCollection.Name)]
public sealed class SecureStoreDeviceBackfillTests
{
    private const string StoredRefresh = "stored-refresh";

    private readonly Mock<IDeviceMetadataReporter> _reporter = new();

    [Fact]
    public async Task GetAccessTokenAsync_AfterASuccessfulRefresh_ReportsTheDeviceOnce()
    {
        _reporter.Setup(r => r.ReportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var renewed = Jwt(DateTime.UtcNow.AddMinutes(15));
        var store = new FakeSecureTokenStore(Jwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        using var sut = new SecureStoreTokenStorageService(store, Refresher(TokenAcquisition.Acquired(renewed)).Object, _reporter.Object);

        var token = await sut.GetAccessTokenAsync();

        token.Should().Be(renewed);
        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAccessTokenAsync_AfterAFailedRefresh_ReportsNothing(bool sessionGone)
    {
        var store = new FakeSecureTokenStore(Jwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var outcome = sessionGone ? TokenAcquisition.NoSession : TokenAcquisition.Unavailable;
        using var sut = new SecureStoreTokenStorageService(store, Refresher(outcome).Object, _reporter.Object);

        var token = await sut.GetAccessTokenAsync();

        token.Should().BeNull();
        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithAFreshStoredToken_DoesNotRefresh_AndReportsNothing()
    {
        var store = new FakeSecureTokenStore(Jwt(DateTime.UtcNow.AddMinutes(10)), StoredRefresh);
        using var sut = new SecureStoreTokenStorageService(store, new Mock<ISessionAwareTokenRefresher>().Object, _reporter.Object);

        await sut.GetAccessTokenAsync();

        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAccessTokenAsync_AfterAFailedReport_RetriesOnTheNextRefresh(bool reportThrows)
    {
        var calls = 0;
        var firstReport = reportThrows ? Task.FromException<bool>(new HttpRequestException("offline")) : Task.FromResult(false);
        _reporter
            .Setup(r => r.ReportAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? firstReport : Task.FromResult(true));
        var store = new FakeSecureTokenStore(Jwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = new ExpiringRefresher(store);
        using var sut = new SecureStoreTokenStorageService(store, refresher, _reporter.Object);

        (await sut.GetAccessTokenAsync()).Should().NotBeNull("a failed report never fails the read it follows");
        _reporter.Verify(r => r.ReportAsync(It.IsAny<CancellationToken>()), Times.Once);

        await sut.GetAccessTokenAsync();

        _reporter.Verify(
            r => r.ReportAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "the first report was not recorded, so the next refresh sends it again");
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterASuccessfulReport_LaterRefreshesStaySilent()
    {
        _reporter.Setup(r => r.ReportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var store = new FakeSecureTokenStore(Jwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        var refresher = new ExpiringRefresher(store);
        using var first = new SecureStoreTokenStorageService(store, refresher, _reporter.Object);
        using var second = new SecureStoreTokenStorageService(store, refresher, _reporter.Object);

        await first.GetAccessTokenAsync();
        await first.GetAccessTokenAsync();
        await second.GetAccessTokenAsync();

        refresher.Refreshes.Should().Be(3, "every read found an expired token and refreshed");
        _reporter.Verify(
            r => r.ReportAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "rotation carries the recorded device forward, so one recorded report per process is enough");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTheReporterNeverCompletes_ReturnsTheTokenWithinTheBound()
    {
        _reporter.Setup(r => r.ReportAsync(It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource<bool>().Task);
        var renewed = Jwt(DateTime.UtcNow.AddMinutes(15));
        var store = new FakeSecureTokenStore(Jwt(DateTime.UtcNow.AddMinutes(-5)), StoredRefresh);
        using var sut = new SecureStoreTokenStorageService(store, Refresher(TokenAcquisition.Acquired(renewed)).Object, _reporter.Object);

        var read = sut.GetAccessTokenAsync();
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        finished.Should().BeSameAs(read, "the wait on the report is bounded");
        (await read).Should().Be(renewed);
    }

    private static Mock<ISessionAwareTokenRefresher> Refresher(TokenAcquisition outcome)
    {
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        return refresher;
    }

    private static string Jwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(notBefore: expires.AddMinutes(-60), expires: expires));

    /// <summary>
    /// Refreshes successfully but stores a token that is already expired, so every read refreshes
    /// again: the shape of "the next access-token lifetime" without waiting one out.
    /// </summary>
    private sealed class ExpiringRefresher(FakeSecureTokenStore store) : ISessionAwareTokenRefresher
    {
        public int Refreshes { get; private set; }

        public Task<string?> AcquireAccessTokenAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the storage uses the session-aware path");

        public async Task<TokenAcquisition> TryAcquireAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Refreshes++;
            var token = Jwt(DateTime.UtcNow.AddMinutes(-1));
            await store.SetTokensAsync(token, StoredRefresh);
            return TokenAcquisition.Acquired(token);
        }
    }

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
