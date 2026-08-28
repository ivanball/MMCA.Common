using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components;
using MMCA.Common.UI.Services.Capabilities;
using Moq;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="SharePageButton"/> (ADR-042): the native share path, the copy-link
/// fallback when no share surface exists, and the accessible-name contract the axe gate depends on.
/// The shared URL must always be absolute (built by <c>IPublicLinkBuilder</c>), never the raw
/// app-relative route.
/// </summary>
public sealed class SharePageButtonTests : BunitTestBase
{
    private readonly Mock<IShareService> _share = new();
    private readonly Mock<IClipboardService> _clipboard = new();

    public SharePageButtonTests()
    {
        Services.AddSingleton(_share.Object);
        Services.AddSingleton(_clipboard.Object);
    }

    [Fact]
    public void RendersAccessibleIconButton()
    {
        var cut = RenderButton();

        cut.Find("button").GetAttribute("aria-label").Should().Be("Share this page");
    }

    [Fact]
    public async Task WhenNativeShareSucceeds_DoesNotFallBackToClipboard()
    {
        _share
            .Setup(x => x.ShareLinkAsync("Intro to Blazor", It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = RenderButton();
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        _clipboard.Verify(
            x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenShareUnavailable_CopiesTheAbsolutePublicLink()
    {
        _share
            .Setup(x => x.ShareLinkAsync(It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _clipboard
            .Setup(x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = RenderButton();
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        // bUnit's NavigationManager base uri is http://localhost/, so the copied link must be
        // the absolute public URL, never the bare app-relative route.
        _clipboard.Verify(
            x => x.SetTextAsync("http://localhost/sessions/42", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SharesTheAbsoluteUrlBuiltFromThePublicLinkBuilder()
    {
        Uri? sharedUri = null;
        _share
            .Setup(x => x.ShareLinkAsync(It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Callback((string _, Uri uri, CancellationToken _) => sharedUri = uri)
            .ReturnsAsync(true);

        var cut = RenderButton();
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        sharedUri.Should().Be(new Uri("http://localhost/sessions/42"));
    }

    [Fact]
    public async Task WithoutARelativePath_DoesNothing()
    {
        var cut = RenderUnderTest<SharePageButton>(p => p
            .Add(c => c.RelativePath, string.Empty)
            .Add(c => c.ShareTitle, "Intro to Blazor"));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        _share.Verify(
            x => x.ShareLinkAsync(It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private IRenderedComponent<SharePageButton> RenderButton() =>
        RenderUnderTest<SharePageButton>(p => p
            .Add(c => c.RelativePath, "/sessions/42")
            .Add(c => c.ShareTitle, "Intro to Blazor"));
}
