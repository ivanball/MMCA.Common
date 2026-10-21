using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.UI.Components.PageState;

namespace MMCA.Common.UI.Tests.Components.PageState;

/// <summary>
/// bUnit tests for <see cref="ListNoRecordsContent"/>: the whole point of the component is that a
/// failed fetch and a genuinely empty list stop looking identical, so the branch on
/// <c>DataGridListPageBase.LoadFailed</c> and its retry affordance are what these pin.
/// </summary>
public sealed class ListNoRecordsContentTests : BunitTestBase
{
    [Fact]
    public void WhenTheListIsEmpty_RendersTheEmptyState()
    {
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, false)
            .Add(c => c.EmptyMessage, "No sessions yet"));

        cut.FindComponents<EmptyState>().Should().ContainSingle();
        cut.Markup.Should().Contain("No sessions yet");
        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    [Fact]
    public void WhenTheListIsEmpty_ForwardsTheIconOverride()
    {
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, false)
            .Add(c => c.EmptyMessage, "No sessions yet")
            .Add(c => c.EmptyIcon, MudBlazor.Icons.Material.Filled.EventBusy));

        cut.FindComponent<EmptyState>().Instance.Icon.Should().Be(MudBlazor.Icons.Material.Filled.EventBusy);
    }

    [Fact]
    public void WhenTheFetchFailed_RendersAnAlertWithRetryInsteadOfTheEmptyState()
    {
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, true)
            .Add(c => c.EmptyMessage, "No sessions yet"));

        cut.FindComponents<EmptyState>().Should().BeEmpty();
        cut.Find("[role=alert]").TextContent.Should().Contain("Could not load this list");
        cut.Markup.Should().NotContain("No sessions yet");
    }

    // -- EmptyConfirmed: a pending data source must not paint the empty frame (U-35) --
    [Fact]
    public void WhenEmptinessIsConfirmed_RendersTheEmptyState()
    {
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, false)
            .Add(c => c.EmptyConfirmed, true)
            .Add(c => c.EmptyMessage, "No sessions yet"));

        cut.FindComponents<EmptyState>().Should().ContainSingle();
        cut.Markup.Should().Contain("No sessions yet");
    }

    [Fact]
    public void WhenEmptinessIsNotConfirmed_RendersNothing()
    {
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, false)
            .Add(c => c.EmptyConfirmed, false)
            .Add(c => c.EmptyMessage, "No sessions yet"));

        cut.FindComponents<EmptyState>().Should().BeEmpty();
        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void WhenTheFetchFailed_ShowsTheFailureEvenIfEmptinessIsNotConfirmed()
    {
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, true)
            .Add(c => c.EmptyConfirmed, false)
            .Add(c => c.EmptyMessage, "No sessions yet"));

        cut.Find("[role=alert]").TextContent.Should().Contain("Could not load this list");
        cut.FindComponents<EmptyState>().Should().BeEmpty();
    }

    [Fact]
    public async Task RetryInvokesTheCallback()
    {
        var retried = 0;
        var cut = RenderUnderTest<ListNoRecordsContent>(p => p
            .Add(c => c.LoadFailed, true)
            .Add(c => c.OnRetry, EventCallback.Factory.Create(this, () => retried++)));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        retried.Should().Be(1);
    }
}
