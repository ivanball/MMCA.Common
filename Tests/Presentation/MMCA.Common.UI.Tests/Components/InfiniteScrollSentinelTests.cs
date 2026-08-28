using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="InfiniteScrollSentinel"/>: the observer-only companion a page renders
/// when it owns its own card markup. What matters here is the contract with the shared
/// <c>infinite-scroll.js</c> module (the fixed <c>OnSentinelVisible</c> callback name), the
/// accessible progress row, and that disposal after the last page is silent.
/// </summary>
public sealed class InfiniteScrollSentinelTests : BunitTestBase
{
    [Fact]
    public void RendersTheSentinelElement()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(_ => { });

        cut.Find("div.infinite-scroll-sentinel").Should().NotBeNull();
    }

    [Fact]
    public void WhileIdle_RendersNoProgressRow()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p.Add(c => c.IsLoading, false));

        cut.FindAll("[role=status]").Should().BeEmpty();
    }

    [Fact]
    public void WhileLoading_RendersAPoliteProgressRow()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p
            .Add(c => c.IsLoading, true)
            .Add(c => c.LoadingLabel, "Loading more sessions"));

        var status = cut.Find("[role=status]");
        status.GetAttribute("aria-live").Should().Be("polite");
        status.GetAttribute("aria-busy").Should().Be("true");
        cut.Markup.Should().Contain("Loading more sessions");
    }

    [Fact]
    public async Task OnSentinelVisible_RaisesTheHostCallback()
    {
        var appended = 0;
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p
            .Add(c => c.OnVisible, EventCallback.Factory.Create(this, () => appended++)));

        await cut.Instance.OnSentinelVisible();

        appended.Should().Be(1);
    }

    [Fact]
    public async Task AfterDisposal_TheCallbackIsSuppressed()
    {
        // The host stops rendering the sentinel the moment the last page is loaded; an
        // observer callback still in flight must not re-enter the disposed component.
        var appended = 0;
        var cut = RenderUnderTest<InfiniteScrollSentinel>(p => p
            .Add(c => c.OnVisible, EventCallback.Factory.Create(this, () => appended++)));

        await cut.Instance.DisposeAsync();
        await cut.Instance.OnSentinelVisible();

        appended.Should().Be(0);
    }

    [Fact]
    public async Task DisposingTwiceIsSilent()
    {
        var cut = RenderUnderTest<InfiniteScrollSentinel>(_ => { });

        await cut.Instance.DisposeAsync();
        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
