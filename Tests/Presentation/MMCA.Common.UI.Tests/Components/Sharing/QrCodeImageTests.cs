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
    // while keeping its aspect ratio, whatever class the caller passes. The rule lives in the
    // component's scoped stylesheet (rubric section 20 bans inline styles), declared !important so it
    // keeps the precedence the inline style had over a caller's class and a parent's ::deep sizing.
    [Fact]
    public void Image_ShrinksToItsContainer_KeepingItsAspectRatio()
    {
        var cut = RenderUnderTest<QrCodeImage>(p => p
            .Add(c => c.Payload, "https://example.com/conference/sessions/42")
            .Add(c => c.AltText, "code")
            .Add(c => c.PixelsPerModule, 14));

        cut.Find("img").Should().NotBeNull("the scoped rule targets the img the component renders itself");

        var css = File.ReadAllText(ScopedStylesheetPath()).Replace(" ", string.Empty, StringComparison.Ordinal);

        css.Should().Contain("img{", "the scoped rule must target the component's own img");
        css.Should().Contain("max-width:100%!important", "the code must never be wider than the space it is given");
        css.Should().Contain("height:auto!important", "shrinking the width must not distort the square code");
    }

    private static string ScopedStylesheetPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MMCA.Common.slnx")))
            {
                return Path.Combine(
                    directory.FullName, "Source", "Presentation", "MMCA.Common.UI", "Components", "Sharing", "QrCodeImage.razor.css");
            }
        }

        throw new InvalidOperationException("MMCA.Common.slnx not found above the test output directory.");
    }
}
