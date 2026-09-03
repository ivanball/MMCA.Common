using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.UI.Components.Sharing;

namespace MMCA.Common.UI.Tests.Components.Sharing;

/// <summary>
/// bUnit tests for <see cref="QrCodeButton"/> (ADR-042): the accessible-name contract, and the
/// invariant that the encoded payload is the ABSOLUTE public URL. A code encoding an app-relative
/// route (or a WebView-internal origin) is unscannable by the OS camera it is meant for.
/// </summary>
/// <remarks>
/// The inline <c>MudDialog</c> renders its body through <c>MudDialogProvider</c>, so every
/// post-click assertion is made against the provider's render tree, not the button's.
/// </remarks>
public sealed class QrCodeButtonTests : BunitTestBase
{
    [Fact]
    public void RendersAccessibleIconButton()
    {
        var cut = RenderButton();

        cut.Find("button").GetAttribute("aria-label").Should().Be("Show QR code for this page");
    }

    [Fact]
    public void BeforeTheFirstClick_NoCodeIsEncoded()
    {
        var providers = RenderMudProviders();
        RenderButton();

        providers.Dialog.FindComponents<QrCodeImage>().Should().BeEmpty();
    }

    [Fact]
    public async Task ClickingEncodesTheAbsolutePublicUrl()
    {
        var providers = RenderMudProviders();
        var cut = RenderButton();

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        var image = providers.Dialog.FindComponent<QrCodeImage>().Instance;
        image.Payload.Should().Be("http://localhost/sessions/42");
        image.AltText.Should().Be("QR code linking to Intro to Blazor");
    }

    [Fact]
    public async Task ClickingRendersTheEncodedPngAndTheUrlAsText()
    {
        var providers = RenderMudProviders();
        var cut = RenderButton();

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        providers.Dialog.Find("img").GetAttribute("src").Should().StartWith("data:image/png;base64,");
        providers.Dialog.Markup.Should().Contain("http://localhost/sessions/42");
    }

    [Fact]
    public async Task ForwardsTheEncodingParametersToTheImage()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<QrCodeButton>(p => p
            .Add(c => c.RelativePath, "/sessions/42")
            .Add(c => c.QrTitle, "Intro to Blazor")
            .Add(c => c.PixelsPerModule, 4)
            .Add(c => c.ErrorCorrection, QrErrorCorrectionLevel.High));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        var image = providers.Dialog.FindComponent<QrCodeImage>().Instance;
        image.PixelsPerModule.Should().Be(4);
        image.ErrorCorrection.Should().Be(QrErrorCorrectionLevel.High);
    }

    [Fact]
    public async Task WithoutARelativePath_NothingIsEncoded()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<QrCodeButton>(p => p
            .Add(c => c.RelativePath, string.Empty)
            .Add(c => c.QrTitle, "Intro to Blazor"));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        providers.Dialog.FindComponents<QrCodeImage>().Should().BeEmpty();
    }

    private IRenderedComponent<QrCodeButton> RenderButton() =>
        RenderUnderTest<QrCodeButton>(p => p
            .Add(c => c.RelativePath, "/sessions/42")
            .Add(c => c.QrTitle, "Intro to Blazor"));
}
