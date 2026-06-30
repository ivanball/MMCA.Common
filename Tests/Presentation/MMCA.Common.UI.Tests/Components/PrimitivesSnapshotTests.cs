using AwesomeAssertions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Render-snapshot (golden-markup) regression for the shared UI primitives (rubric §28). Each test renders
/// a primitive and asserts its normalized markup matches a committed baseline under <c>Snapshots/</c>, so an
/// unintended structural change to a reused component fails the build. Deterministic and OS-independent
/// (markup, not pixels): runs in the in-solution unit tier on every platform. Refresh after an intentional
/// change with <c>UPDATE_SNAPSHOTS=1</c> and commit the updated <c>.html</c>.
/// </summary>
public sealed class PrimitivesSnapshotTests : BunitTestBase
{
    [Fact]
    public void EmptyState_DefaultMessage_MatchesSnapshot()
    {
        var cut = RenderUnderTest<EmptyState>(_ => { });

        var result = MarkupSnapshot.Match(cut.Markup, "EmptyState_Default");

        result.IsMatch.Should().BeTrue(result.Message);
    }

    [Fact]
    public void EmptyState_CustomMessage_MatchesSnapshot()
    {
        var cut = RenderUnderTest<EmptyState>(p => p.Add(c => c.Message, "Nothing to see"));

        var result = MarkupSnapshot.Match(cut.Markup, "EmptyState_CustomMessage");

        result.IsMatch.Should().BeTrue(result.Message);
    }

    [Fact]
    public void PageHeader_Title_MatchesSnapshot()
    {
        var cut = RenderUnderTest<PageHeader>(p => p.Add(c => c.Title, "Speakers"));

        var result = MarkupSnapshot.Match(cut.Markup, "PageHeader_Title");

        result.IsMatch.Should().BeTrue(result.Message);
    }

    [Fact]
    public void PageErrorState_Message_MatchesSnapshot()
    {
        var cut = RenderUnderTest<PageErrorState>(p => p.Add(c => c.Message, "Something went wrong"));

        var result = MarkupSnapshot.Match(cut.Markup, "PageErrorState_Message");

        result.IsMatch.Should().BeTrue(result.Message);
    }

    [Fact]
    public void PageLoadingState_Label_MatchesSnapshot()
    {
        var cut = RenderUnderTest<PageLoadingState>(p => p.Add(c => c.Label, "Loading speakers"));

        var result = MarkupSnapshot.Match(cut.Markup, "PageLoadingState_Label");

        result.IsMatch.Should().BeTrue(result.Message);
    }
}
