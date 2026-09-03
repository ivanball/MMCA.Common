using AwesomeAssertions;
using MMCA.Common.UI.Components.PageState;

namespace MMCA.Common.UI.Tests.Components.PageState;

public sealed class EmptyStateTests : BunitTestBase
{
    [Fact]
    public void Renders_TheProvidedMessage()
    {
        var cut = RenderUnderTest<EmptyState>(p => p.Add(c => c.Message, "Nothing to see"));

        cut.Markup.Should().Contain("Nothing to see");
    }

    [Fact]
    public void Renders_DefaultMessage_WhenNotSpecified()
    {
        var cut = RenderUnderTest<EmptyState>(_ => { });

        cut.Markup.Should().Contain("No records found.");
    }
}
