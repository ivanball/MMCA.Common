using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Exercises the real <c>APIClient</c> pipeline around a token refresh (H37): the refresh POST goes
/// through the same named client that carries <see cref="AuthDelegatingHandler"/>, so unless that
/// request opts out of the bearer, the handler-scope storage instance re-enters its own in-flight
/// hydrate and the acquisition never completes. <see cref="WasmTokenStorageService"/> stands in for the
/// MAUI storage (identical single-flight shape; the MAUI type only compiles for the MAUI TFMs).
/// </summary>
public sealed class TokenRefreshPipelineTests
{
    [Fact]
    public async Task GetAccessTokenAsync_WithAStaleSession_RefreshesThroughThePipelineWithoutDeadlocking()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new AuthenticationResponse("new-access", "new-refresh", DateTime.UtcNow.AddMinutes(15))),
        });
        var secureStore = new Mock<ISecureTokenStore>();
        secureStore.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("old-access");
        secureStore.Setup(s => s.GetRefreshTokenAsync()).ReturnsAsync("old-refresh");

        var services = new ServiceCollection();
        services.AddTransient<AuthDelegatingHandler>();
        services.AddScoped<ITokenStorageService, WasmTokenStorageService>();
        services.AddScoped<ITokenRefresher, DirectApiTokenRefresher>();
        services.AddScoped(_ => secureStore.Object);
        services.AddScoped(_ => new Mock<ISessionCookieSync>().Object);
        services.AddHttpClient("APIClient", c => c.BaseAddress = new Uri("https://api.test/"))
            .AddHttpMessageHandler<AuthDelegatingHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<ITokenStorageService>();

        var token = await storage.GetAccessTokenAsync()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        token.Should().Be("new-access");
        stub.Requests.Should().ContainSingle();
        stub.LastRequest.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest.Uri!.AbsolutePath.Should().Be("/auth/refresh");
        stub.LastRequest.Authorization.Should().BeNull();
    }
}
