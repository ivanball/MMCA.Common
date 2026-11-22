using System.Net;
using System.Text;
using System.Web;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Tests.Infrastructure;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// The layout listener (design 2.2.3): a "second factor required" raise for the page scope's storage
/// sends an interactive page to <c>/profile/two-factor?required=true&amp;returnUrl=&lt;current&gt;</c>;
/// nothing happens outside an interactive render, on the account page itself or on sign-in, or after
/// the listener is disposed. The last test runs the whole path: a coded 403 through the real handler
/// reaches the listener and redirects.
/// </summary>
public sealed class MultiFactorRequiredListenerTests : BunitTestBase
{
    private const string MultiFactorProblem =
        """{ "status": 403, "errors": [ { "code": "Authorization.MultiFactorRequired", "message": "Use your second factor." } ] }""";

    private readonly StubTokenStorageService _storage = new($"listener-{Guid.NewGuid():N}");

    public MultiFactorRequiredListenerTests() =>
        Services.AddSingleton<ITokenStorageService>(_storage);

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public async Task ARaise_InAnInteractiveRender_RedirectsToTheAccountPage_WithTheCurrentPageAsReturnUrl()
    {
        var cut = RenderListener(interactive: true, at: "/orders/42?tab=items");

        await cut.InvokeAsync(() => MultiFactorRequiredSignal.Raise(_storage));

        var target = new Uri(Navigation.Uri);
        target.AbsolutePath.Should().Be("/profile/two-factor");
        var query = HttpUtility.ParseQueryString(target.Query);
        query["required"].Should().Be("true");
        query["returnUrl"].Should().Be("/orders/42?tab=items", "the user comes back to the page that was refused");
    }

    [Fact]
    public async Task ARaise_OutsideAnInteractiveRender_DoesNotNavigate()
    {
        var cut = RenderListener(interactive: false, at: "/orders");

        await cut.InvokeAsync(() => MultiFactorRequiredSignal.Raise(_storage));

        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/orders", "a static prerender cannot navigate; the interactive call raises again");
    }

    [Theory]
    [InlineData("/profile/two-factor?required=true")]
    [InlineData("/login?returnUrl=%2Forders")]
    public async Task ARaise_OnTheAccountPageOrSignIn_DoesNotNavigate(string at)
    {
        var cut = RenderListener(interactive: true, at: at);
        var before = Navigation.History.Count;

        await cut.InvokeAsync(() => MultiFactorRequiredSignal.Raise(_storage));

        Navigation.History.Count.Should().Be(before, "the user is already where the second factor is set up or used");
    }

    [Fact]
    public async Task ARaiseForAnotherScopesStorage_DoesNotNavigate()
    {
        var cut = RenderListener(interactive: true, at: "/orders");

        await cut.InvokeAsync(() => MultiFactorRequiredSignal.Raise(new StubTokenStorageService()));

        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/orders");
    }

    [Fact]
    public async Task AfterDisposal_TheListenerIsUnsubscribed_AndARaiseDoesNothing()
    {
        var cut = RenderListener(interactive: true, at: "/orders");
        MultiFactorRequiredSignal.HasSubscriber(_storage).Should().BeTrue("the listener subscribes when it initializes");

        await DisposeComponentsAsync();
        MultiFactorRequiredSignal.Raise(_storage);

        MultiFactorRequiredSignal.HasSubscriber(_storage).Should().BeFalse();
        new Uri(Navigation.Uri).AbsolutePath.Should().Be("/orders");
        cut.Should().NotBeNull();
    }

    [Fact]
    public async Task ACoded403ThroughTheHandler_ReachesTheListener_AndRedirects()
    {
        RenderListener(interactive: true, at: "/reports");
        var redirected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Navigation.LocationChanged += (_, e) => redirected.TrySetResult(e.Location);
        var handler = new AuthDelegatingHandler(_storage)
        {
            InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(MultiFactorProblem, Encoding.UTF8, "application/problem+json"),
            }),
        };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost/api/reports"));

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);
        // Bounded so a regression fails instead of hanging the run; a pass completes on the dispatch.
        var location = await redirected.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        new Uri(location).AbsolutePath.Should().Be("/profile/two-factor");
        HttpUtility.ParseQueryString(new Uri(location).Query)["returnUrl"].Should().Be("/reports");
    }

    private IRenderedComponent<MultiFactorRequiredListener> RenderListener(bool interactive, string at)
    {
        // RendererInfo after every Services.Add call (it freezes the provider).
        SetRendererInfo(new RendererInfo(interactive ? "Server" : "Static", isInteractive: interactive));
        Navigation.NavigateTo(at);
        return Render<MultiFactorRequiredListener>();
    }
}
