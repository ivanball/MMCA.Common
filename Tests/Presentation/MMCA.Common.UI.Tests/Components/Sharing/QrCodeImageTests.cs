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

    // OBS-9: the bitmap's intrinsic width is modules x PixelsPerModule (about 300 px at the default,
    // more for a long payload or a larger module size), so on a narrow phone the code overflowed its
    // card and was clipped, which can make it unscannable. The image must shrink to its container
    // while keeping its aspect ratio, whatever class the caller passes.
    [Fact]
    public void Image_ShrinksToItsContainer_KeepingItsAspectRatio()
    {
        var cut = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/conference/sessions/42")
            .Add(c => c.AltText, "code")
            .Add(c => c.PixelsPerModule, 14));

        var style = (cut.Find("img").GetAttribute("style") ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);

        style.Should().Contain("max-width:100%", "the code must never be wider than the space it is given");
        style.Should().Contain("height:auto", "shrinking the width must not distort the square code");
    }
}
