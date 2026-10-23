using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.UI.Components.PageState;

namespace MMCA.Common.UI.Tests.Components.PageState;

/// <summary>
/// bUnit tests for <see cref="DetailLoadStateContent"/>: a record that does not exist and a record
/// that could not be fetched must render differently, and only the second offers a retry.
/// </summary>
public sealed class DetailLoadStateContentTests : BunitTestBase
{
    [Fact]
    public void WhileLoading_RendersTheSpinnerOnly()
    {
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.Loading)
            .Add(c => c.ChildContent, "record"));

        cut.FindComponents<PageLoadingState>().Should().ContainSingle();
        cut.FindAll("button").Should().BeEmpty();
        cut.Markup.Should().NotContain("record");
    }

    [Fact]
    public void WhenNotFound_RendersTheNotFoundAlertWithoutRetry()
    {
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.NotFound)
            .Add(c => c.NotFoundMessage, "Session not found"));

        cut.Find("[role=alert]").TextContent.Should().Contain("Session not found");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void WhenNotFound_WithoutAMessage_FallsBackToTheGenericText()
    {
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.NotFound));

        cut.Find("[role=alert]").TextContent.Should().Contain("This item was not found");
    }

    [Fact]
    public void WhenFailed_RendersAnErrorWithRetryRatherThanNotFound()
    {
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.Failed)
            .Add(c => c.NotFoundMessage, "Session not found"));

        cut.Find("[role=alert]").TextContent.Should().Contain("Could not load this page");
        cut.Markup.Should().NotContain("Session not found");
        cut.Find("button").TextContent.Should().Contain("Retry");
    }

    [Fact]
    public void WhenFailed_UsesTheSuppliedMessage()
    {
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.Failed)
            .Add(c => c.FailedMessage, "Could not load the session"));

        cut.Find("[role=alert]").TextContent.Should().Contain("Could not load the session");
    }

    [Fact]
    public async Task RetryInvokesTheCallback()
    {
        var retried = 0;
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.Failed)
            .Add(c => c.OnRetry, EventCallback.Factory.Create(this, () => retried++)));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        retried.Should().Be(1);
    }

    [Fact]
    public void WhenLoaded_RendersTheChildContent()
    {
        var cut = RenderUnderTest<DetailLoadStateContent>(p => p
            .Add(c => c.State, DetailLoadState.Loaded)
            .Add(c => c.ChildContent, "record"));

        cut.Markup.Should().Contain("record");
        cut.FindAll("[role=alert]").Should().BeEmpty();
        cut.FindComponents<PageLoadingState>().Should().BeEmpty();
    }
}
