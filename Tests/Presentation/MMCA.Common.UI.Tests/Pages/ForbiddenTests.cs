using AwesomeAssertions;
using MMCA.Common.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages;

/// <summary>
/// Render-smoke for the in-shell 403 page (rubric §25): it announces "Access Denied" via a
/// role="alert" heading and offers a way back home, rather than leaving the user on a dead end.
/// </summary>
public sealed class ForbiddenTests : BunitTestBase
{
    [Fact]
    public void RendersAccessDeniedHeadingAndHomeLink()
    {
        var cut = RenderUnderTest<Forbidden>(_ => { });

        cut.Markup.Should().Contain("Access Denied");
        cut.Markup.Should().Contain("role=\"alert\"");
        cut.Markup.Should().Contain("href=\"/\"");
    }
}
