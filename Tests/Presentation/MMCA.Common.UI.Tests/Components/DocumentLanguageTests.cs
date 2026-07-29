using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Covers <see cref="DocumentLanguage"/> (ADR-027 Decision 10). A MAUI Blazor Hybrid head serves a static
/// <c>index.html</c> whose <c>lang</c> cannot be templated, so this component is the only thing that makes
/// the document language follow a culture switch there. No automated accessibility gate can catch a
/// <em>wrong</em> <c>lang</c> (axe checks presence and syntax only), which is why it is asserted here.
/// </summary>
public sealed class DocumentLanguageTests : BunitTestBase
{
    [Theory]
    [InlineData("es", "es")]
    [InlineData("en-US", "en")]
    [InlineData("es-MX", "es")]
    public void SetsTheDocumentLanguage_ToTheActiveUiCultureLanguage(string culture, string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            RenderUnderTest<DocumentLanguage>(_ => { });

            var invocation = JSInterop.Invocations["setDocumentLanguage"].Should().ContainSingle().Subject;
            invocation.Arguments.Should().ContainSingle().Which.Should().Be(expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void RendersNoMarkup()
    {
        var cut = RenderUnderTest<DocumentLanguage>(_ => { });

        cut.Markup.Should().BeEmpty();
    }
}
