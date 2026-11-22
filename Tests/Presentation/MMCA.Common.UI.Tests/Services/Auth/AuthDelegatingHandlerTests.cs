using System.Net;
using AwesomeAssertions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="AuthDelegatingHandler"/>: the stored access token is attached as a Bearer
/// header on every outgoing request, requests stay anonymous when no token exists, and the handler
/// is a pure pass-through (no refresh, no retry) even on 401 responses; token re-acquisition lives
/// in the storage/refresher chain, not here.
/// </summary>
public sealed class AuthDelegatingHandlerTests
{
    /// <summary>The coded "second factor required" refusal the API answers a 403 with (v1.314.0 F2).</summary>
    private const string MultiFactorProblem =
        """{ "status": 403, "errors": [ { "code": "Authorization.MultiFactorRequired", "message": "Use your second factor.", "type": "Forbidden" } ] }""";

    private static (HttpMessageInvoker Invoker, StubHttpMessageHandler Inner, Mock<ITokenStorageService> TokenStorage) CreateSut(
        string? token,
        HttpStatusCode responseStatusCode = HttpStatusCode.OK)
    {
        var inner = StubHttpMessageHandler.RespondingWith(responseStatusCode);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(token);
        var handler = new AuthDelegatingHandler(tokenStorage.Object) { InnerHandler = inner };
        return (new HttpMessageInvoker(handler, disposeHandler: true), inner, tokenStorage);
    }

    private static HttpRequestMessage CreateRequest() => new(HttpMethod.Get, new Uri("http://localhost/api/widgets"));

    [Fact]
    public async Task SendAsync_WithStoredToken_AttachesBearerHeader()
    {
        var (invoker, inner, _) = CreateSut("stored-access-token");
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        inner.LastRequest.Authorization.Should().NotBeNull();
        inner.LastRequest.Authorization!.Scheme.Should().Be("Bearer");
        inner.LastRequest.Authorization.Parameter.Should().Be("stored-access-token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_WithoutUsableToken_LeavesRequestAnonymous(string? token)
    {
        var (invoker, inner, _) = CreateSut(token);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        inner.LastRequest.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_On401_PassesResponseThroughWithoutRefreshOrRetry()
    {
        // Pins the pass-through contract: this handler never refreshes or retries; 401 handling is
        // the token storage / refresher chain's job.
        var (invoker, inner, tokenStorage) = CreateSut("stored-access-token", HttpStatusCode.Unauthorized);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inner.CallCount.Should().Be(1);
        tokenStorage.Verify(s => s.GetAccessTokenAsync(), Times.Once);
    }

    [Fact]
    public async Task SendAsync_WithSkipBearerOption_PassesThroughWithoutReadingTheTokenStorage()
    {
        // H37: the token refresh POST opts out, so the refresh never re-enters the storage awaiting it.
        var (invoker, inner, tokenStorage) = CreateSut("stored-access-token");
        using var request = CreateRequest();
        request.Options.Set(AuthDelegatingHandler.SkipBearer, true);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1);
        inner.LastRequest.Authorization.Should().BeNull();
        tokenStorage.Verify(s => s.GetAccessTokenAsync(), Times.Never);
    }

    // M187: a caller that already set the bearer (the forced-refresh replay) keeps it; replacing it
    // with the stored token resends the very token the server just rejected.
    [Fact]
    public async Task SendAsync_WithAuthorizationAlreadySet_KeepsItAndSkipsTheStorageRead()
    {
        var (invoker, inner, tokenStorage) = CreateSut("stored-access-token");
        using var request = CreateRequest();
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "freshly-refreshed");

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        inner.LastRequest.Authorization!.Parameter.Should().Be("freshly-refreshed");
        tokenStorage.Verify(s => s.GetAccessTokenAsync(), Times.Never);
    }

    // Store Run 5 D1b: in a browser host the handler resolves its OWN storage in the IHttpClientFactory
    // handler scope, while a service attaches the bearer from the component's storage, which is the one
    // the auth state listens to. The 401 must reach that owner, or the shell never signs out.
    [Fact]
    public async Task SendAsync_On401_ToABearerAServiceAttached_EndsTheSessionOfTheStorageThatHeldIt()
    {
        var componentRefresher = new Mock<ISessionAwareTokenRefresher>();
        componentRefresher.Setup(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(TokenAcquisition.NoSession);
        var componentStorage = new WasmTokenStorageService(SucceedingCookieSync(), componentRefresher.Object);
        await componentStorage.SetTokensAsync(CreateUniqueJwt(), "refresh-token");
        var ended = 0;
        componentStorage.SessionEnded += (_, _) => ended++;

        var handlerRefresher = new Mock<ISessionAwareTokenRefresher>();
        var handlerScopeStorage = new WasmTokenStorageService(SucceedingCookieSync(), handlerRefresher.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("APIClient")).Returns(() =>
            new HttpClient(new AuthDelegatingHandler(handlerScopeStorage) { InnerHandler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Unauthorized) })
            {
                BaseAddress = new Uri("https://api.test/"),
            });
        var service = new ProfileService(factory.Object, componentStorage);

        var status = await service.GetProfileStatusAsync(TestContext.Current.CancellationToken);

        status.Should().Be(HttpStatusCode.Unauthorized);
        ended.Should().Be(1, "the refused refresh ends the session the component holds");
        componentRefresher.Verify(r => r.TryAcquireAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
        handlerRefresher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendAsync_On401_ToASkipBearerRequest_ReportsNothing()
    {
        // The refresh POST itself: its 401 is the refresher's own "no session" answer.
        var refresher = new Mock<ISessionAwareTokenRefresher>();
        var storage = new WasmTokenStorageService(SucceedingCookieSync(), refresher.Object);
        var token = CreateUniqueJwt();
        await storage.SetTokensAsync(token, "refresh-token");
        var handler = new AuthDelegatingHandler(storage) { InnerHandler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Unauthorized) };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        using var request = CreateRequest();
        request.Options.Set(AuthDelegatingHandler.SkipBearer, true);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        refresher.VerifyNoOtherCalls();
        (await storage.GetAccessTokenAsync()).Should().Be(token);
    }

    // The owner registry is process-wide, so a sweep must drop only what can never be used again: an
    // owner whose storage was collected, or an entry older than any token lives. Live entries stay. The
    // entries here are unique to this test, so other tests running alongside are unaffected.
    private const int AboveTheSweepThreshold = 1100;

    private static readonly long PastTheRetention = (long)TimeSpan.FromHours(25).TotalMilliseconds;

    [Fact]
    public void Sweep_AboveTheThreshold_DropsDeadAndExpiredOwners_AndKeepsLiveOnes()
    {
        var live = new WasmTokenStorageService(SucceedingCookieSync(), new Mock<ISessionAwareTokenRefresher>().Object);
        var run = Guid.NewGuid().ToString("N");
        var liveTokens = Enumerable.Range(0, AboveTheSweepThreshold).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sweep-live-{run}-{i}")).ToList();
        foreach (var token in liveTokens)
        {
            AuthDelegatingHandler.TrackTokenOwner(token, live);
        }

        var expired = $"sweep-expired-{run}";
        AuthDelegatingHandler.TrackTokenOwnerAt(expired, new WeakReference<ISessionAwareTokenStorage>(live), Environment.TickCount64 - PastTheRetention);
        var dead = $"sweep-dead-{run}";
        // A reference whose storage is gone, as after a circuit or scope ends and its storage is collected.
        AuthDelegatingHandler.TrackTokenOwnerAt(dead, new WeakReference<ISessionAwareTokenStorage>(null!), Environment.TickCount64);

        AuthDelegatingHandler.SweepIfLarge();

        AuthDelegatingHandler.IsTrackedOwner(dead).Should().BeFalse("its storage is gone, so no 401 can reach it");
        AuthDelegatingHandler.IsTrackedOwner(expired).Should().BeFalse("it outlived any access token");
        liveTokens.Should().OnlyContain(token => AuthDelegatingHandler.HasLiveOwner(token), "a live, recent owner is kept");
        GC.KeepAlive(live);
    }

    [Fact]
    public void Sweep_AboveTheThreshold_DropsExpiredRenewals_AndKeepsRecentOnes()
    {
        var run = Guid.NewGuid().ToString("N");
        var recent = Enumerable.Range(0, AboveTheSweepThreshold).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sweep-renewed-{run}-{i}")).ToList();
        foreach (var token in recent)
        {
            AuthDelegatingHandler.MarkRenewedAt(token, Environment.TickCount64);
        }

        var old = $"sweep-renewed-old-{run}";
        AuthDelegatingHandler.MarkRenewedAt(old, Environment.TickCount64 - PastTheRetention);

        AuthDelegatingHandler.SweepIfLarge();

        AuthDelegatingHandler.IsMarkedRenewed(old).Should().BeFalse();
        recent.Should().OnlyContain(token => AuthDelegatingHandler.IsMarkedRenewed(token));
    }

    [Fact]
    public void TrackTokenOwner_IgnoresABlankTokenAndAStorageThatCannotRenew()
    {
        var plain = new Mock<ITokenStorageService>().Object;
        var token = $"untracked-{Guid.NewGuid():N}";

        AuthDelegatingHandler.TrackTokenOwner(token, plain);
        AuthDelegatingHandler.TrackTokenOwner("   ", new WasmTokenStorageService(SucceedingCookieSync(), new Mock<ISessionAwareTokenRefresher>().Object));

        AuthDelegatingHandler.IsTrackedOwner(token).Should().BeFalse("a storage without the session-aware capability cannot renew");
        AuthDelegatingHandler.IsTrackedOwner("   ").Should().BeFalse();
    }

    // v1.314.0 F2 (design 2.2.3): a 403 carrying the coded "second factor required" refusal raises the
    // signal for the storage that attached the bearer, but only when that storage has a listener (a host
    // that called AddTwoFactorUI) and only for a Problem Details body. Each test uses its own storage and
    // a unique bearer, so the process-wide owner map and signal table are never shared between tests.
    [Fact]
    public async Task SendAsync_On403WithTheMultiFactorCode_AndASubscriber_RaisesTheSignalOnce()
    {
        var (invoker, storage) = CreateForbiddenSut(() => ProblemContent(MultiFactorProblem));
        var raised = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => raised++);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        raised.Should().Be(1, "the coded refusal is what sends the user to set up or use their second factor");
    }

    [Fact]
    public async Task SendAsync_On403_MatchesThePropertyNamesIgnoringCase()
    {
        var (invoker, storage) = CreateForbiddenSut(() => ProblemContent(
            """{ "Errors": [ { "Code": "Authorization.MultiFactorRequired" } ] }"""));
        var raised = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => raised++);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        raised.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_On403WithAnotherCode_DoesNotRaise()
    {
        var (invoker, storage) = CreateForbiddenSut(() => ProblemContent(
            """{ "status": 403, "errors": [ { "code": "Authorization.Forbidden", "message": "No." } ] }"""));
        var raised = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => raised++);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        raised.Should().Be(0, "a plain forbid is not a second-factor refusal");
    }

    [Fact]
    public async Task SendAsync_On403ThatIsNotProblemDetails_DoesNotRaise()
    {
        var (invoker, storage) = CreateForbiddenSut(() =>
            new StringContent(MultiFactorProblem, System.Text.Encoding.UTF8, "application/json"));
        var raised = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => raised++);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        raised.Should().Be(0, "only an application/problem+json body is read");
    }

    [Fact]
    public async Task SendAsync_On403WithABodyThatIsNotJson_DoesNotThrowOrRaise()
    {
        var (invoker, storage) = CreateForbiddenSut(() => ProblemContent("<html>denied</html>"));
        var raised = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => raised++);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        raised.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_On403WithNoSubscriber_NeverReadsTheBody()
    {
        // A host that never called AddTwoFactorUI has no listener: its 403 bodies are never touched.
        var content = new ThrowOnReadContent();
        var (invoker, _) = CreateForbiddenSut(() => content);
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        content.ReadAttempts.Should().Be(0, "without a subscriber the handler must return without reading the content");
    }

    [Fact]
    public async Task SendAsync_On403_AfterRaising_TheCallerCanStillReadTheSameBody()
    {
        var (invoker, storage) = CreateForbiddenSut(() => ProblemContent(MultiFactorProblem));
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => { });
        using var request = CreateRequest();

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Be(MultiFactorProblem, "the body is buffered, so the service still reads the coded refusal");
    }

    [Fact]
    public async Task SendAsync_On403_ToABearerAServiceAttached_RaisesForTheStorageThatHeldIt()
    {
        // Same owner rule as the 401 path: the handler's own scope storage is not the page's.
        var componentStorage = new WasmTokenStorageService(SucceedingCookieSync(), new Mock<ISessionAwareTokenRefresher>().Object);
        await componentStorage.SetTokensAsync(CreateUniqueJwt(), "refresh-token");
        var handlerScopeStorage = new StubTokenStorageService($"handler-{Guid.NewGuid():N}");
        var componentRaised = 0;
        var handlerRaised = 0;
        using var componentSubscription = MultiFactorRequiredSignal.Subscribe(componentStorage, () => componentRaised++);
        using var handlerSubscription = MultiFactorRequiredSignal.Subscribe(handlerScopeStorage, () => handlerRaised++);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("APIClient")).Returns(() =>
            new HttpClient(new AuthDelegatingHandler(handlerScopeStorage)
            {
                InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = ProblemContent(MultiFactorProblem) }),
            })
            {
                BaseAddress = new Uri("https://api.test/"),
            });
        var service = new ProfileService(factory.Object, componentStorage);

        var status = await service.GetProfileStatusAsync(TestContext.Current.CancellationToken);

        status.Should().Be(HttpStatusCode.Forbidden);
        componentRaised.Should().Be(1, "the page whose storage attached the bearer is the one redirected");
        handlerRaised.Should().Be(0);
    }

    private static (HttpMessageInvoker Invoker, StubTokenStorageService Storage) CreateForbiddenSut(Func<HttpContent> content)
    {
        var storage = new StubTokenStorageService($"mfa-{Guid.NewGuid():N}");
        var inner = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content() });
        var handler = new AuthDelegatingHandler(storage) { InnerHandler = inner };
        return (new HttpMessageInvoker(handler, disposeHandler: true), storage);
    }

    private static StringContent ProblemContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/problem+json");

    /// <summary>A problem+json body that counts and refuses every attempt to read it.</summary>
    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent() =>
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json");

        public int ReadAttempts { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempts++;
            throw new InvalidOperationException("The 403 body must not be read without a subscriber.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            ReadAttempts++;
            throw new InvalidOperationException("The 403 body must not be read without a subscriber.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static ISessionCookieSync SucceedingCookieSync()
    {
        var sync = new Mock<ISessionCookieSync>();
        sync.Setup(s => s.SyncAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        return sync.Object;
    }

    private static string CreateUniqueJwt() =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            claims: [new System.Security.Claims.Claim("jti", Guid.NewGuid().ToString("N"))],
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(10)));

    /// <summary>A module service the way a page uses one: the bearer comes from the component's storage.</summary>
    private sealed class ProfileService(IHttpClientFactory factory, ITokenStorageService storage)
        : MMCA.Common.UI.Services.Api.AuthenticatedServiceBase(factory, storage)
    {
        public async Task<HttpStatusCode> GetProfileStatusAsync(CancellationToken cancellationToken)
        {
            using var client = await CreateAuthenticatedClientAsync();
            using var response = await client.GetAsync(new Uri("profile", UriKind.Relative), cancellationToken);
            return response.StatusCode;
        }
    }
}
