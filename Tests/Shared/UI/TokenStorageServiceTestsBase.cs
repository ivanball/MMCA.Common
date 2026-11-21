using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using Moq;

namespace MMCA.Common.Tests.Shared.UI;

/// <summary>
/// The in-memory token contract the WASM storage and the Blazor Server storage (on the interactive
/// circuit, where no <c>HttpContext</c> exists) share: the access token is hydrated single-flight
/// through <see cref="ITokenRefresher"/>, a fresh JWT short-circuits it and a near-expiry one refreshes
/// proactively, a definitive "no session" is remembered briefly while a transient failure is not, the
/// refresh token never surfaces, set and clear round-trip through <see cref="ISessionCookieSync"/>, a
/// hydrate in flight across a set or clear never overwrites it, and a held session that ended remotely
/// signs out. Each subclass supplies the storage under test.
/// </summary>
/// <remarks>Linked as source into MMCA.Common.UI.Tests and MMCA.Common.UI.Web.Tests.</remarks>
public abstract class TokenStorageServiceTestsBase
{
    /// <summary>
    /// Builds the storage under test over the given collaborators. For the server storage this is the
    /// interactive circuit: an accessor with no <c>HttpContext</c>.
    /// </summary>
    protected abstract ISessionAwareTokenStorage CreateStorage(
        ISessionCookieSync sessionCookieSync,
        ITokenRefresher tokenRefresher,
        TimeProvider? timeProvider);

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
        var sut = CreateStorage(cookieSync.Object, refresher.Object, timeProvider: null);

        var first = sut.GetAccessTokenAsync();
        var second = sut.GetAccessTokenAsync();
        pendingAcquisition.SetResult("hydrated-token");

        (await first).Should().Be("hydrated-token");
        (await second).Should().Be("hydrated-token");
        refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterCompletion_SecondStaleCallStartsNewHydrate()
    {
        // The single-flight slot must be cleared once its own hydrate finishes, so a later caller
        // with a still-stale token hydrates again rather than awaiting a completed task forever.
        // The hydrated value is not a JWT, so it stays stale (a null hydrate is now remembered
        // briefly, L132, and would no longer exercise the slot).
        var (sut, mocks) = CreateSut();

        (await sut.GetAccessTokenAsync()).Should().Be("hydrated-token");
        (await sut.GetAccessTokenAsync()).Should().Be("hydrated-token");

        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // L132: an anonymous visitor's "no session" answer is remembered for a short grace, instead of
    // one /auth/session/token round trip per API call.
    [Fact]
    public async Task GetAccessTokenAsync_WhenNoSessionExists_RemembersTheNegativeAnswerBriefly()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (sut, mocks) = CreateSut(refresherToken: null, timeProvider: clock);

        (await sut.GetAccessTokenAsync()).Should().BeNull();
        (await sut.GetAccessTokenAsync()).Should().BeNull();
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);

        clock.Advance(TimeSpan.FromSeconds(16));
        (await sut.GetAccessTokenAsync()).Should().BeNull();
        mocks.Refresher.Verify(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // L132 review follow-up: only a DEFINITIVE "no session" starts the grace. A transient failure
    // (429, 5xx, dropped connection) must leave the next read free to hydrate again, or a signed-in
    // user reads as anonymous for the grace period.
    [Fact]
    public async Task GetAccessTokenAsync_AfterATransientFailure_HydratesAgainOnTheNextRead()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.SetupSequence(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenAcquisition.Unavailable)
            .ReturnsAsync(TokenAcquisition.Acquired("hydrated-token"));
        var sut = CreateStorage(Mock.Of<ISessionCookieSync>(), refresher.Object, clock);

        (await sut.GetAccessTokenAsync()).Should().BeNull();
        (await sut.GetAccessTokenAsync()).Should().Be("hydrated-token");

        refresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // == Refresh token never surfaces client-side or on the circuit ==
    [Fact]
    public async Task GetRefreshTokenAsync_EvenAfterSet_ReturnsNull()
    {
        var (sut, _) = CreateSut();
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddMinutes(10)), "refresh-token");

        var result = await sut.GetRefreshTokenAsync();

        result.Should().BeNull("the HttpOnly refresh cookie must never be readable in the browser or on the circuit");
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

    [Fact]
    public async Task SetTokensAsync_WhenTheCookieWriteFails_ThrowsSoLoginReportsStorageUnavailable()
    {
        // M129: a login whose cookie write failed would silently sign out at the first access-token
        // expiry, because no cookie exists to refresh from.
        var (sut, mocks) = CreateSut();
        mocks.CookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        var act = () => sut.SetTokensAsync("access-token", "refresh-token");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ClearTokensAsync_WhenTheCookieClearFails_StillClearsMemoryWithoutThrowing()
    {
        // Logout stays best-effort by contract (IAuthUIService.LogoutAsync never fails).
        var (sut, mocks) = CreateSut();
        mocks.CookieSync.Setup(c => c.ClearAsync()).ReturnsAsync(false);

        var act = () => sut.ClearTokensAsync();

        await act.Should().NotThrowAsync();
    }

    // == M234: a hydrate in flight across a set or clear must not overwrite it ==
    [Fact]
    public async Task GetAccessTokenAsync_WhenALoginCompletesDuringAnAnonymousHydrate_KeepsTheNewTokenAndNoGrace()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pending = new TaskCompletionSource<TokenAcquisition>();
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var sut = CreateStorage(cookieSync.Object, refresher.Object, clock);
        var signedIn = CreateJwt(DateTime.UtcNow.AddHours(1));

        var hydrate = sut.GetAccessTokenAsync();
        await sut.SetTokensAsync(signedIn, "refresh-token");
        pending.SetResult(TokenAcquisition.NoSession);

        (await hydrate).Should().Be(signedIn, "the stale anonymous answer must not overwrite a login that landed meanwhile");
        (await sut.GetAccessTokenAsync()).Should().Be(signedIn, "no anonymous grace may be armed by the stale hydrate");
        refresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenALogoutCompletesDuringAHydrate_DoesNotResurrectTheClearedToken()
    {
        var pending = new TaskCompletionSource<TokenAcquisition>();
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.SetupSequence(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .Returns(pending.Task)
            .ReturnsAsync(TokenAcquisition.NoSession);
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.ClearAsync()).ReturnsAsync(true);
        var sut = CreateStorage(cookieSync.Object, refresher.Object, timeProvider: null);

        var hydrate = sut.GetAccessTokenAsync();
        await sut.ClearTokensAsync();
        pending.SetResult(TokenAcquisition.Acquired(CreateJwt(DateTime.UtcNow.AddHours(1))));

        (await hydrate).Should().BeNull("a token acquired before the logout must not be restored after it");
        (await sut.GetAccessTokenAsync()).Should().BeNull();
    }

    // == M234 review (R2): a hydrate that STARTS while the cookie clear is still awaiting ==
    [Fact]
    public async Task GetAccessTokenAsync_WhenAHydrateStartsDuringTheClearAndFinishesAfterIt_DiscardsTheStaleToken()
    {
        var (sut, pendingClear, pendingAcquisition) = CreateGatedClearSut();

        var clear = sut.ClearTokensAsync();
        var hydrate = sut.GetAccessTokenAsync();
        pendingClear.SetResult(true);
        await clear;
        pendingAcquisition.SetResult(TokenAcquisition.Acquired(CreateJwt(DateTime.UtcNow.AddHours(1))));

        (await hydrate).Should().BeNull("the answer was read from the session the logout was still clearing");
        (await sut.GetAccessTokenAsync()).Should().BeNull("the stale token must not have been stored");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenAHydrateStartsAndFinishesDuringTheClear_DoesNotSurviveTheClear()
    {
        var (sut, pendingClear, pendingAcquisition) = CreateGatedClearSut();

        var clear = sut.ClearTokensAsync();
        var hydrate = sut.GetAccessTokenAsync();
        pendingAcquisition.SetResult(TokenAcquisition.Acquired(CreateJwt(DateTime.UtcNow.AddHours(1))));
        await hydrate;
        pendingClear.SetResult(true);
        await clear;

        (await sut.GetAccessTokenAsync()).Should().BeNull("a completed logout leaves no token in memory");
    }

    // == A-34: a held session that ended remotely signs the shell (or the circuit) out ==
    [Fact]
    public async Task GetAccessTokenAsync_WhenAHeldSessionWasRevokedRemotely_ClearsTheTokenAndNotifiesLogout()
    {
        // "Sign out everywhere" on another device (or a deleted account): the refresh answers a
        // definitive no-session for a session this client held. The shell must flip to anonymous
        // instead of staying signed in behind 401s.
        var (sut, provider, refresher) = await CreateSignedInSutAsync(TokenAcquisition.NoSession);
        Task<AuthenticationState>? notified = null;
        provider.AuthenticationStateChanged += task => notified = task;

        (await sut.GetAccessTokenAsync()).Should().BeNull();

        notified.Should().NotBeNull("a session that ended on the server is a sign-out");
        (await notified!).User.Identity!.IsAuthenticated.Should().BeFalse();
        (await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeFalse();
        (await sut.GetAccessTokenAsync()).Should().BeNull("the ended session's token is gone from memory");
        refresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTheRefreshFailsTransiently_DoesNotSignOut()
    {
        var (sut, provider, _) = await CreateSignedInSutAsync(TokenAcquisition.Unavailable);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;

        (await sut.GetAccessTokenAsync()).Should().BeNull();

        notifications.Should().Be(0, "a 429, 5xx or dropped connection says nothing about the session");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenAnAnonymousVisitorHasNoSession_DoesNotNotifyLogout()
    {
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(TokenAcquisition.NoSession);
        var sut = CreateStorage(Mock.Of<ISessionCookieSync>(), refresher.Object, timeProvider: null);
        var provider = new JwtAuthenticationStateProvider(sut);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;

        (await sut.GetAccessTokenAsync()).Should().BeNull();

        notifications.Should().Be(0, "nobody was signed in, so there is nothing to sign out");
    }

    // == Store Run 5 D1b: an API 401 to a held token makes one refresh attempt ==
    [Fact]
    public async Task ApiUnauthorized_ForAFreshHeldToken_WhenTheRefreshIsRefused_EndsTheSession()
    {
        // A locked user's token is still fresh by the clock, so no read ever refreshed it: the shell
        // stayed signed in behind "you are not signed in" errors. The 401 itself must trigger the refresh.
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(TokenAcquisition.NoSession);
        var (sut, provider) = await CreateSignedInWithFreshTokenAsync(refresher);
        Task<AuthenticationState>? notified = null;
        provider.AuthenticationStateChanged += task => notified = task;

        using var response = await SendThroughHandlerAsync(sut, HttpStatusCode.Unauthorized);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the caller still gets the server's answer");
        notified.Should().NotBeNull("a refused refresh after an API 401 is a sign-out");
        (await notified!).User.Identity!.IsAuthenticated.Should().BeFalse();
        (await sut.GetAccessTokenAsync()).Should().BeNull("the rejected token is gone");
        refresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiUnauthorized_WhenTheRefreshSucceeds_TheNewTokenServes_AndItsOwn401DoesNotRefreshAgain()
    {
        var renewed = CreateUniqueJwt(DateTime.UtcNow.AddMinutes(15));
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.SetupSequence(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenAcquisition.Acquired(renewed))
            .ReturnsAsync(TokenAcquisition.NoSession);
        var (sut, provider) = await CreateSignedInWithFreshTokenAsync(refresher);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;

        using (var response = await SendThroughHandlerAsync(sut, HttpStatusCode.Unauthorized))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await sut.GetAccessTokenAsync()).Should().Be(renewed, "the next call carries the refreshed token");

        using (var response = await SendThroughHandlerAsync(sut, HttpStatusCode.Unauthorized))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        refresher.Verify(
            r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "a 401 to the token a refresh just produced must not drive another refresh");
        notifications.Should().Be(0);
    }

    [Fact]
    public async Task ApiUnauthorized_ForATokenTheStorageDoesNotHold_ChangesNothing()
    {
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        var (sut, provider) = await CreateSignedInWithFreshTokenAsync(refresher);
        var held = await sut.GetAccessTokenAsync();
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;

        using (var response = await SendThroughHandlerAsync(sut, HttpStatusCode.Unauthorized, bearer: CreateUniqueJwt(DateTime.UtcNow.AddMinutes(10))))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        refresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
        (await sut.GetAccessTokenAsync()).Should().Be(held);
        notifications.Should().Be(0);
    }

    /// <summary>A JWT that expires at <paramref name="expires"/>.</summary>
    protected internal static string CreateJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    /// <summary>
    /// The storage over a cookie sync that succeeds and a refresher that answers
    /// <paramref name="refresherToken"/>, with both mocks for verification.
    /// </summary>
    protected (ISessionAwareTokenStorage Sut, Mocks Mocks) CreateSut(
        string? refresherToken = "hydrated-token",
        TimeProvider? timeProvider = null)
    {
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        cookieSync.Setup(c => c.ClearAsync()).ReturnsAsync(true);
        var refresher = new Mock<ITokenRefresher>();
        refresher
            .Setup(r => r.AcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(refresherToken);
        return (CreateStorage(cookieSync.Object, refresher.Object, timeProvider), new Mocks(cookieSync, refresher));
    }

    /// <summary>A signed-in storage whose refresh answers <paramref name="outcome"/>.</summary>
    protected Task<(ISessionAwareTokenStorage Sut, JwtAuthenticationStateProvider Provider, Mock<ISessionAwareTokenRefresher> Refresher)> CreateSignedInSutAsync(
        TokenAcquisition outcome)
    {
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        return CreateSignedInSutAsync(refresher);
    }

    /// <summary>A signed-in storage over <paramref name="refresher"/>, with the provider observing it.</summary>
    protected async Task<(ISessionAwareTokenStorage Sut, JwtAuthenticationStateProvider Provider, Mock<ISessionAwareTokenRefresher> Refresher)> CreateSignedInSutAsync(
        Mock<ISessionAwareTokenRefresher> refresher)
    {
        ArgumentNullException.ThrowIfNull(refresher);
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var sut = CreateStorage(cookieSync.Object, refresher.Object, timeProvider: null);
        var provider = new JwtAuthenticationStateProvider(sut);

        // Signed in at login; the access token is now inside the 30-second refresh skew.
        await sut.SetTokensAsync(CreateJwt(DateTime.UtcNow.AddSeconds(10)), "refresh-token");
        return (sut, provider, refresher);
    }

    /// <summary>
    /// A JWT that expires at <paramref name="expires"/> and is unique: the delegating handler remembers
    /// tokens process-wide, so two tests must never share one.
    /// </summary>
    protected internal static string CreateUniqueJwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))],
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    /// <summary>Sends one request through a real <see cref="AuthDelegatingHandler"/> over <paramref name="storage"/>.</summary>
    /// <param name="storage">The storage the handler reads the bearer from.</param>
    /// <param name="answer">The status the API answers with.</param>
    /// <param name="bearer">A bearer the request already carries; the storage's token when null.</param>
    protected static async Task<HttpResponseMessage> SendThroughHandlerAsync(
        ITokenStorageService storage,
        HttpStatusCode answer,
        string? bearer = null)
    {
        using var invoker = new HttpMessageInvoker(new AuthDelegatingHandler(storage) { InnerHandler = new AnsweringHandler(answer) });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/widgets"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await invoker.SendAsync(request, CancellationToken.None);
    }

    /// <summary>A signed-in storage holding a FRESH token (no read refreshes it), observed by a provider.</summary>
    private async Task<(ISessionAwareTokenStorage Sut, JwtAuthenticationStateProvider Provider)> CreateSignedInWithFreshTokenAsync(
        Mock<ISessionAwareTokenRefresher> refresher)
    {
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var sut = CreateStorage(cookieSync.Object, refresher.Object, timeProvider: null);
        var provider = new JwtAuthenticationStateProvider(sut);
        await sut.SetTokensAsync(CreateUniqueJwt(DateTime.UtcNow.AddMinutes(10)), "refresh-token");
        return (sut, provider);
    }

    private (ISessionAwareTokenStorage Sut, TaskCompletionSource<bool> PendingClear, TaskCompletionSource<TokenAcquisition> PendingAcquisition) CreateGatedClearSut()
    {
        var pendingClear = new TaskCompletionSource<bool>();
        var pendingAcquisition = new TaskCompletionSource<TokenAcquisition>();
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        refresher.SetupSequence(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()))
            .Returns(pendingAcquisition.Task)
            .ReturnsAsync(TokenAcquisition.NoSession);
        var cookieSync = new Mock<ISessionCookieSync>();
        cookieSync.Setup(c => c.ClearAsync()).Returns(pendingClear.Task);
        return (CreateStorage(cookieSync.Object, refresher.Object, timeProvider: null), pendingClear, pendingAcquisition);
    }

    /// <summary>The cookie sync and refresher behind a <see cref="CreateSut"/> storage.</summary>
    protected sealed record Mocks(Mock<ISessionCookieSync> CookieSync, Mock<ITokenRefresher> Refresher);

    /// <summary>The API: answers every request with one status.</summary>
    private sealed class AnsweringHandler(HttpStatusCode answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(answer) { RequestMessage = request });
    }

    /// <summary>A settable clock (MMCA.Common.UI.Web.Tests does not reference the TimeProvider testing package).</summary>
    protected sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
