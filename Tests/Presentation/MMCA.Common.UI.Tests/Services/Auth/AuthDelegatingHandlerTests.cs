using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
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
