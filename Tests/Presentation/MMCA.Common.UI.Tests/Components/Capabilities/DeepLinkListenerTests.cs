using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Navigation;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Covers <see cref="DeepLinkListener"/> (ADR-042): draining the cold-start buffered route
/// after first render, navigating live route requests, and unsubscribing on dispose so a
/// recycled dispatcher never navigates a dead component.
/// </summary>
public sealed class DeepLinkListenerTests : BunitTestBase
{
    [Fact]
    public void ColdStart_DrainsThePendingRouteIntoNavigation()
    {
        var dispatcher = new DeepLinkDispatcher();
        dispatcher.Publish("/conference/sessions/42");
        Services.AddSingleton<IDeepLinkDispatcher>(dispatcher);

        RenderUnderTest<DeepLinkListener>(_ => { });

        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        navigation.Uri.Should().EndWith("/conference/sessions/42");
        dispatcher.TryConsumePending(out _).Should().BeFalse("the listener consumed the buffer");
    }

    [Fact]
    public void LiveRequest_NavigatesThroughTheRenderer()
    {
        var dispatcher = new DeepLinkDispatcher();
        Services.AddSingleton<IDeepLinkDispatcher>(dispatcher);

        var cut = RenderUnderTest<DeepLinkListener>(_ => { });
        dispatcher.Publish("/happening-now");

        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => navigation.Uri.Should().EndWith("/happening-now"));
    }

    [Fact]
    public async Task AfterDispose_RoutesBufferInsteadOfNavigating()
    {
        var dispatcher = new DeepLinkDispatcher();
        Services.AddSingleton<IDeepLinkDispatcher>(dispatcher);

        RenderUnderTest<DeepLinkListener>(_ => { });
        await DisposeComponentsAsync();

        dispatcher.Publish("/notifications");

        dispatcher.TryConsumePending(out var route).Should().BeTrue("no live listener remains");
        route.Should().Be("/notifications");
    }

    /// <summary>
    /// D5 (Run 15): the cold-start drain ran NavigateTo unguarded inside OnAfterRender, so a buffered
    /// route the navigation refused escaped into the renderer (the Blazor error bar on MAUI, every later
    /// deep link dead). The drain now drops it the way the live path already did, and the listener stays
    /// subscribed for the next link.
    /// </summary>
    [Fact]
    public void ColdStart_NavigationThatThrows_IsDroppedAndTheListenerStaysLive()
    {
        var dispatcher = new DeepLinkDispatcher();
        dispatcher.Publish("/conference/sessions/42");
        Services.AddSingleton<IDeepLinkDispatcher>(dispatcher);
        var navigation = new ThrowingOnceNavigationManager();
        Services.AddSingleton<NavigationManager>(navigation);

        IRenderedComponent<DeepLinkListener>? cut = null;
        var act = () => cut = RenderUnderTest<DeepLinkListener>(_ => { });

        act.Should().NotThrow();
        dispatcher.Publish("/happening-now");
        cut!.WaitForAssertion(() => navigation.Uri.Should().EndWith("/happening-now"));
    }

    private sealed class ThrowingOnceNavigationManager : NavigationManager
    {
        private bool _thrown;

        public ThrowingOnceNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new ArgumentException("The route was refused.", nameof(uri));
            }

            Uri = ToAbsoluteUri(uri).ToString();
        }
    }
}
