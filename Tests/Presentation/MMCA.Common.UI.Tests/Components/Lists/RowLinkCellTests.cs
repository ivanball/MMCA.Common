using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Components.Lists;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Lists;

/// <summary>
/// Pins <see cref="RowLinkCell"/> (item 15, rule 9): the grid cell link wrapper admin list pages
/// copied five times per page renders exactly one <c>MudLink</c> with the given href, inherited
/// colour and no underline, around the cell content.
/// </summary>
public sealed class RowLinkCellTests : BunitTestBase
{
    private const string Href = "/conference/rooms/7";

    [Fact]
    public void Renders_AnchorWithHrefAndChildContent()
    {
        var cut = RenderUnderTest<RowLinkCell>(p => p
            .Add(c => c.Href, Href)
            .AddChildContent("<span class=\"probe-cell\">Main hall</span>"));

        var anchor = cut.FindAll("a").Should().ContainSingle().Subject;
        anchor.GetAttribute("href").Should().Be(Href);
        anchor.QuerySelector("span.probe-cell")!.TextContent.Should().Be("Main hall");
        cut.FindComponents<MudLink>().Should().ContainSingle();
    }

    [Fact]
    public void Renders_InheritColorAndNoUnderlineClasses()
    {
        var cut = RenderUnderTest<RowLinkCell>(p => p
            .Add(c => c.Href, Href)
            .AddChildContent("Main hall"));

        var anchor = cut.Find("a");
        anchor.ClassList.Should().Contain("mud-inherit-text").And.Contain("mud-link-underline-none");

        var link = cut.FindComponent<MudLink>().Instance;
        link.Color.Should().Be(Color.Inherit);
        link.Underline.Should().Be(Underline.None);
        link.Href.Should().Be(Href);
    }
}
