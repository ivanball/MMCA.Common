using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Capabilities;

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
}
