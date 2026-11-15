using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.OAuth;

/// <summary>
/// bUnit tests for the <c>/auth/oauth-complete</c> landing page: a provider error or a missing code
/// is reported without an exchange, a code that does not belong to an attempt started on this
/// client is dropped, and a bound code is exchanged once and lands on the sanitized return URL.
/// </summary>
public sealed class OAuthCompleteTests : BunitTestBase
{
    private static readonly AuthenticationResponse Signed = new("access", "refresh", DateTime.UtcNow.AddMinutes(15));

    private readonly Mock<IAuthUIService> _auth = new();
    private readonly OAuthFlowStateStore _flowState = new(new InMemoryCacheStore());

    public OAuthCompleteTests()
    {
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(_flowState);
    }

    [Fact]
    public async Task ProviderError_IsReported_WithoutAnExchange()
    {
        var cut = RenderAt("/auth/oauth-complete?error=access_denied&code=c-1");

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[role=alert]").TextContent.Should().Contain("Authentication failed: access_denied"));
        VerifyNoExchange();
        cut.Markup.Should().Contain("href=\"/login\"", "the failure offers the way back to the login page");
    }

    [Fact]
    public async Task MissingCode_IsReported_WithoutAnExchange()
    {
        var cut = RenderAt("/auth/oauth-complete?state=s-1");

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[role=alert]").TextContent.Should().Contain("Authentication failed: missing exchange code."));
        VerifyNoExchange();
    }

    [Fact]
    public async Task CodeWithNoAttemptStartedOnThisClient_IsDropped()
    {
        var cut = RenderAt("/auth/oauth-complete?code=c-1&state=forged");

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[role=alert]").TextContent.Should().Contain("did not come from a sign-in started on this device"));
        VerifyNoExchange();
    }

    [Fact]
    public async Task BoundCode_IsExchanged_AndLandsOnTheReturnUrl()
    {
        var state = await _flowState.BeginAsync(Xunit.TestContext.Current.CancellationToken);
        _auth.Setup(a => a.ExchangeOAuthCodeAsync("c-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Signed));

        var cut = RenderAt($"/auth/oauth-complete?code=c-1&state={state}&returnUrl=%2Forders%3Fpage%3D2");

        await cut.WaitForAssertionAsync(() => CurrentPathAndQuery().Should().Be("/orders?page=2"));
        _auth.Verify(a => a.ExchangeOAuthCodeAsync("c-1", It.IsAny<CancellationToken>()), Times.Once());
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Fact]
    public async Task BoundCode_WithAnOffSiteReturnUrl_LandsOnTheRoot()
    {
        var state = await _flowState.BeginAsync(Xunit.TestContext.Current.CancellationToken);
        _auth.Setup(a => a.ExchangeOAuthCodeAsync("c-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Signed));

        var cut = RenderAt($"/auth/oauth-complete?code=c-1&state={state}&returnUrl=https%3A%2F%2Fevil.example%2F");

        await cut.WaitForAssertionAsync(() => CurrentPathAndQuery().Should().Be("/"));
    }

    [Fact]
    public async Task ExchangeRefused_ShowsTheApisMessage_AndStays()
    {
        var state = await _flowState.BeginAsync(Xunit.TestContext.Current.CancellationToken);
        _auth.Setup(a => a.ExchangeOAuthCodeAsync("c-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Authentication.InvalidExchangeCode", "The code was already used.", "Test")));

        var cut = RenderAt($"/auth/oauth-complete?code=c-1&state={state}");

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[role=alert]").TextContent.Should().Contain("The code was already used."));
        CurrentPathAndQuery().Should().StartWith("/auth/oauth-complete");
    }

    [Fact]
    public async Task ExchangeThrowing_ShowsTheGenericFailure_AndStays()
    {
        var state = await _flowState.BeginAsync(Xunit.TestContext.Current.CancellationToken);
        _auth.Setup(a => a.ExchangeOAuthCodeAsync("c-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var cut = RenderAt($"/auth/oauth-complete?code=c-1&state={state}");

        await cut.WaitForAssertionAsync(() =>
            cut.Find("[role=alert]").TextContent.Should().Contain("Failed to complete sign in. Please try again."));
        CurrentPathAndQuery().Should().StartWith("/auth/oauth-complete");
    }

    [Fact]
    public async Task ALaterRender_DoesNotRunTheCompletionAgain()
    {
        var cut = RenderAt("/auth/oauth-complete?error=access_denied");

        await cut.WaitForAssertionAsync(() => cut.FindAll("[role=alert]").Should().ContainSingle());
        cut.Render();

        cut.FindAll("[role=alert]").Should().ContainSingle("a later render does not re-run the completion");
    }

    private IRenderedComponent<OAuthComplete> RenderAt(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return RenderUnderTest<OAuthComplete>(_ => { });
    }

    private string CurrentPathAndQuery() => new Uri(Services.GetRequiredService<NavigationManager>().Uri).PathAndQuery;

    private void VerifyNoExchange() =>
        _auth.Verify(a => a.ExchangeOAuthCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());

    private sealed class InMemoryCacheStore : ILocalCacheStore
    {
        private readonly Dictionary<string, string> _entries = [];

        public bool IsAvailable => true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _entries[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }
}
