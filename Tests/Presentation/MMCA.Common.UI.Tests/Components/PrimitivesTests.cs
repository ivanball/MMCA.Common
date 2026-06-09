using AwesomeAssertions;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

public sealed class PrimitivesTests : BunitTestBase
{
    [Fact]
    public void PageErrorState_RendersMessage()
    {
        var cut = RenderUnderTest<PageErrorState>(p => p.Add(c => c.Message, "Boom"));

        cut.Markup.Should().Contain("Boom");
    }

    [Fact]
    public void PageLoadingState_RendersAriaBusyWithLabel()
    {
        var cut = RenderUnderTest<PageLoadingState>(p => p.Add(c => c.Label, "Fetching"));

        cut.Markup.Should().Contain("aria-busy").And.Contain("Fetching");
    }

    [Fact]
    public void PageHeader_RendersTitle()
    {
        var cut = RenderUnderTest<PageHeader>(p => p.Add(c => c.Title, "Speakers"));

        cut.Markup.Should().Contain("Speakers");
    }
}
