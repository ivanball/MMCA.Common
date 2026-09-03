using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Components.Sharing;

namespace MMCA.Common.UI.Tests.Components.Sharing;

/// <summary>
/// Covers the shared QR primitive: it must encode locally into a PNG data URI (no network, no
/// image service), always carry alt text, and re-encode when the payload changes.
/// </summary>
public sealed class QrCodeImageTests : BunitTestBase
{
    [Fact]
    public void Renders_PngDataUri_WithAltText()
    {
        var cut = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/conference/sessions/42")
            .Add(c => c.AltText, "QR code opening session 42"));

        var img = cut.Find("img");

        img.GetAttribute("src").Should().StartWith("data:image/png;base64,");
        img.GetAttribute("alt").Should().Be("QR code opening session 42");
    }

    [Fact]
    public void Renders_Nothing_WhenPayloadIsBlank()
    {
        var cut = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "   ")
            .Add(c => c.AltText, "unused"));

        cut.FindAll("img").Should().BeEmpty();
    }

    [Fact]
    public void ReEncodes_WhenPayloadChanges()
    {
        var cut = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/a")
            .Add(c => c.AltText, "code"));
        var first = cut.Find("img").GetAttribute("src");

        cut.Render(p => p.Add(c => c.Payload, "https://example.com/b"));

        cut.Find("img").GetAttribute("src").Should().StartWith("data:image/png;base64,").And.NotBe(first);
    }

    [Fact]
    public void HigherErrorCorrection_ProducesADifferentCode()
    {
        var medium = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/a")
            .Add(c => c.AltText, "code"))
            .Find("img").GetAttribute("src");

        var high = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/a")
            .Add(c => c.AltText, "code")
            .Add(c => c.ErrorCorrection, QrErrorCorrectionLevel.High))
            .Find("img").GetAttribute("src");

        high.Should().NotBe(medium);
    }

    [Fact]
    public void Passes_CssClassThrough()
    {
        var cut = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/a")
            .Add(c => c.AltText, "code")
            .Add(c => c.Class, "mmca-qr d-print-block"));

        cut.Find("img").GetAttribute("class").Should().Be("mmca-qr d-print-block");
    }
}
