using AwesomeAssertions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Covers the shipped <c>ErrorSummaryMessages()</c> reader in <c>MMCA.Common.Testing.UI</c>. The
/// reason it exists at all is the shape asymmetry it hides: <see cref="ErrorSummary"/> renders
/// SEVERAL messages as a list but a SINGLE one as plain text inside the alert, so a form test that
/// queried only <c>li</c> would read an empty summary exactly when one rule is broken.
/// </summary>
public sealed class ErrorSummaryExtensionsTests : BunitTestBase
{
    private const string Title = "Please fix the following";

    [Fact]
    public void ErrorSummaryMessages_IsEmpty_WhenNoSummaryIsRendered()
    {
        var cut = RenderUnderTest<ErrorSummary>(_ => { });

        cut.ErrorSummaryMessages().Should().BeEmpty();
    }

    [Fact]
    public void ErrorSummaryMessages_ReadsTheSingleMessageShape_AndStripsTheTitle()
    {
        // A single broken rule renders as plain text inside the alert, NOT as a list item.
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Title, Title)
            .Add(c => c.Messages, ["Name is required."]));

        cut.ErrorSummaryMessages().Should().ContainSingle().Which.Should().Be("Name is required.");
    }

    [Fact]
    public void ErrorSummaryMessages_ReadsTheListShape_OneEntryPerRule()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Title, Title)
            .Add(c => c.Messages, ["Name is required.", "Email is required."]));

        cut.ErrorSummaryMessages().Should().Equal("Name is required.", "Email is required.");
    }

    [Fact]
    public void ErrorSummaryMessages_IsEmpty_WhenTheAlertCarriesNoSummaryTitle()
    {
        // The title class is the marker that identifies the summary's alert among any others on the
        // page, so a titleless summary is deliberately not claimed by this reader.
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Messages, ["Name is required."]));

        cut.ErrorSummaryMessages().Should().BeEmpty();
    }
}
