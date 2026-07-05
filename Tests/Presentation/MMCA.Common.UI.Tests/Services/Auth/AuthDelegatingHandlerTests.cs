#pragma warning disable CA2000 // Dispose objects before losing scope - test doubles do not hold real resources

using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth;
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
}
