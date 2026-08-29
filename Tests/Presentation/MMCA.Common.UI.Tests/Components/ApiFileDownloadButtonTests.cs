using System.Net;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Fallbacks;
using Moq;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="ApiFileDownloadButton"/> (ADR-042): browsers get a plain download
/// link built on the browser-reachable API base, while native heads (link-intercepting hosts) fetch
/// the bytes, stage a temp file, and open the share sheet with the caller's content type. Every
/// failure path has to toast rather than escape: on the native heads an unhandled exception in an
/// OnClick callback is fatal to the host.
/// </summary>
public sealed class ApiFileDownloadButtonTests : BunitTestBase
{
    private static readonly byte[] PayloadBytes = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray();

    public ApiFileDownloadButtonTests()
    {
        // The button injects both capabilities unconditionally (it branches on InterceptsLinks at
        // render time), so the shared null fallbacks stand in for the web head; the native-head
        // arrangements below override them with plain Adds (last registration wins).
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton<IShareService, NullShareService>();
    }

    [Fact]
    public void OnWeb_RendersDownloadLinkOnBrowserReachableApiBase()
    {
        // The Server head's ApiEndpoint may be container-internal; the anchor must prefer
        // WasmApiEndpoint (the externally reachable gateway URL).
        ArrangeWebHead();

        var cut = RenderButton();

        var anchor = cut.Find("a");
        anchor.GetAttribute("href").Should().Be("https://gateway.test/Sessions/42/ics");
        anchor.GetAttribute("aria-label").Should().Be("Add to calendar");
    }

    [Fact]
    public void OnWeb_WithoutAnAriaLabel_FallsBackToTheLocalizedDefault()
    {
        ArrangeWebHead();

        var cut = RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Sessions/42/ics")
            .Add(c => c.FileName, "session-42.ics")
            .Add(c => c.ShareTitle, "Intro to Blazor"));

        cut.Find("a").GetAttribute("aria-label").Should().Be("Download");
    }

    [Fact]
    public async Task OnNativeHead_FetchesTheFileAndOpensTheShareSheet()
    {
        ArrangeWebHead();
        ArrangeLinkInterceptingHead();

        using var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PayloadBytes),
        });
        Services.AddSingleton(HttpTestDoubles.ClientFactory(handler));

        string? sharedPath = null;
        var share = new Mock<IShareService>();
        share
            .Setup(x => x.ShareFileAsync("Intro to Blazor", It.IsAny<string>(), "text/calendar", It.IsAny<CancellationToken>()))
            .Callback((string _, string path, string _, CancellationToken _) => sharedPath = path)
            .ReturnsAsync(true);
        Services.AddSingleton(share.Object);

        var cut = RenderButton();
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        handler.Requests.Should().ContainSingle()
            .Which.Uri!.AbsoluteUri.Should().Be("https://gateway.test/Sessions/42/ics");
        share.Verify(
            x => x.ShareFileAsync("Intro to Blazor", It.IsAny<string>(), "text/calendar", It.IsAny<CancellationToken>()),
            Times.Once);

        // The staged temp file carries the downloaded document.
        sharedPath.Should().NotBeNull();
        (await File.ReadAllBytesAsync(sharedPath!, Xunit.TestContext.Current.CancellationToken)).Should().Equal(PayloadBytes);
        File.Delete(sharedPath!);
    }

    [Fact]
    public async Task OnNativeHead_WhenNoShareSurfaceAccepts_WarnsWithTheCallersMessage()
    {
        var toast = ArrangeNativeHead(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PayloadBytes),
        });

        var share = new Mock<IShareService>();
        share
            .Setup(x => x.ShareFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Services.AddSingleton(share.Object);

        var cut = RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Sessions/43/ics")
            .Add(c => c.FileName, "session-43.ics")
            .Add(c => c.ShareTitle, "Intro to Blazor")
            .Add(c => c.UnavailableMessage, "Calendar export is not available on this device"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        VerifyToast(toast, "Calendar export is not available on this device");
        File.Delete(Path.Combine(Path.GetTempPath(), "session-43.ics"));
    }

    [Fact]
    public async Task OnNativeHead_WhenTheDownloadTimesOut_WarnsInsteadOfKillingTheHost()
    {
        // No token is passed to GetByteArrayAsync, so an HttpClient timeout surfaces as an
        // OperationCanceledException. This is an OnClick callback, and on the native heads an
        // escaping exception is fatal to the host.
        var toast = ArrangeNativeHead(_ => throw new TaskCanceledException("timeout"));

        var cut = RenderButton();
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        VerifyToast(toast, "Could not download the file");
    }

    [Fact]
    public async Task OnNativeHead_WhenTheServerFails_WarnsInsteadOfKillingTheHost()
    {
        var toast = ArrangeNativeHead(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var cut = RenderButton();
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        VerifyToast(toast, "Could not download the file");
    }

    [Fact]
    public async Task OnNativeHead_WhenStagingTheFileFails_WarnsInsteadOfKillingTheHost()
    {
        // An IOException from the temp-file write escaped the HttpRequestException-only catch.
        // A file name that is a directory separator makes the write fail deterministically.
        var toast = ArrangeNativeHead(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PayloadBytes),
        });

        var cut = RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Sessions/42/ics")
            .Add(c => c.FileName, "no-such-directory/session-42.ics")
            .Add(c => c.ShareTitle, "Intro to Blazor"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        VerifyToast(toast, "Could not download the file");
    }

    [Fact]
    public async Task OnNativeHead_ReplacesAStaleStagedFileBeforeSharing()
    {
        // Every tap writes to the same per-entity path, so a leftover from a previous tap must not
        // survive into the share. Deleting AFTER the share is deliberately not done: on Android
        // the intent returns as soon as it launches and the delete would race the receiving app.
        var stalePath = Path.Combine(Path.GetTempPath(), "session-99.ics");
        await File.WriteAllBytesAsync(stalePath, "STALE"u8.ToArray(), Xunit.TestContext.Current.CancellationToken);

        ArrangeNativeHead(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PayloadBytes),
        });

        string? sharedPath = null;
        var share = new Mock<IShareService>();
        share
            .Setup(x => x.ShareFileAsync(
                "Intro to Blazor", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string path, string _, CancellationToken _) => sharedPath = path)
            .ReturnsAsync(true);
        Services.AddSingleton(share.Object);

        var cut = RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Sessions/99/ics")
            .Add(c => c.FileName, "session-99.ics")
            .Add(c => c.ShareTitle, "Intro to Blazor"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        sharedPath.Should().Be(stalePath);
        (await File.ReadAllBytesAsync(stalePath, Xunit.TestContext.Current.CancellationToken)).Should().Equal(PayloadBytes);
        File.Delete(stalePath);
    }

    [Fact]
    public async Task OnNativeHead_DefaultsToTheOctetStreamContentType()
    {
        ArrangeNativeHead(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PayloadBytes),
        });

        string? contentType = null;
        var share = new Mock<IShareService>();
        share
            .Setup(x => x.ShareFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string type, CancellationToken _) => contentType = type)
            .ReturnsAsync(true);
        Services.AddSingleton(share.Object);

        var cut = RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Exports/44")
            .Add(c => c.FileName, "export-44.bin")
            .Add(c => c.ShareTitle, "Export"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        contentType.Should().Be("application/octet-stream");
        File.Delete(Path.Combine(Path.GetTempPath(), "export-44.bin"));
    }

    private IRenderedComponent<ApiFileDownloadButton> RenderButton() =>
        RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Sessions/42/ics")
            .Add(c => c.FileName, "session-42.ics")
            .Add(c => c.ShareTitle, "Intro to Blazor")
            .Add(c => c.ContentType, "text/calendar")
            .Add(c => c.AriaLabel, "Add to calendar"));

    private void ArrangeWebHead()
    {
        // The client factory is injected even on the web path (only the click handler uses it), so
        // a never-called double keeps the anchor-rendering tests focused on the URL.
        Services.AddSingleton(HttpTestDoubles.ClientFactory(
            new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        ArrangeApiSettings();
    }

    private void ArrangeApiSettings() =>
        Services.AddSingleton(Options.Create(new ApiSettings
        {
            ApiEndpoint = "http://internal-gateway/",
            WasmApiEndpoint = "https://gateway.test/",
        }));

    private void ArrangeLinkInterceptingHead()
    {
        var externalLink = new Mock<IExternalLinkService>();
        externalLink.SetupGet(x => x.InterceptsLinks).Returns(true);
        Services.AddSingleton(externalLink.Object);
    }

    private Mock<IToastService> ArrangeNativeHead(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArrangeWebHead();
        ArrangeLinkInterceptingHead();

        var handler = new CapturingHttpMessageHandler(respond);
        Services.AddSingleton(HttpTestDoubles.ClientFactory(handler));

        // Last registration wins, so this recording double replaces the base's real toast service.
        var toast = new Mock<IToastService>();
        Services.AddSingleton(toast.Object);

        return toast;
    }

    private static void VerifyToast(Mock<IToastService> toast, string message) =>
        toast.Verify(t => t.Warning(message), Times.Once);
}
