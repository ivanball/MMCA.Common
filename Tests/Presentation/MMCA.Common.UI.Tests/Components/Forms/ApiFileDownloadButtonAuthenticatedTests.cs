using System.Net;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components.Forms;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// D12 (Run 15): the opt-in authenticated download of <see cref="ApiFileDownloadButton"/>. A plain
/// browser link carries cookies but never a bearer token, so on a Blazor Server circuit (no
/// same-origin proxy) an authenticated endpoint answered 401. With <c>RequiresAuthentication</c> the
/// browser branch fetches through the authenticated API client and saves through the shipped
/// <c>saveFile</c> module instead; without it the plain link is kept. Also covers the opt-in native
/// staging folder, purged before each write.
/// </summary>
public sealed class ApiFileDownloadButtonAuthenticatedTests : BunitTestBase
{
    private const string SaveFileModulePath = "./_content/MMCA.Common.UI/file-download.js";

    private static readonly byte[] PayloadBytes = "Name,Email\r\nAda,ada@example.com\r\n"u8.ToArray();

    private readonly Mock<IToastService> _toast = new();
    private int _requestsSeen;

    public ApiFileDownloadButtonAuthenticatedTests()
    {
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        Services.AddSingleton<IShareService, NullShareService>();
        Services.AddSingleton(_toast.Object);

        // A Blazor Server head: no SameOriginApiEndpoint, so a link would reach the gateway bare.
        Services.AddSingleton(Options.Create(new ApiSettings
        {
            ApiEndpoint = "http://internal-gateway/",
            WasmApiEndpoint = "https://gateway.test/",
        }));
    }

    [Fact]
    public async Task RequiresAuthentication_OnBrowser_FetchesThroughTheClientAndSavesWithoutALink()
    {
        var handler = ArrangeClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PayloadBytes) });
        var module = JSInterop.SetupModule(SaveFileModulePath);
        module.SetupVoid("saveFile", _ => true).SetVoidResult();

        var cut = RenderButton(requiresAuthentication: true);

        cut.FindAll("a").Should().BeEmpty("an authenticated download never renders a bare link");
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        handler.Requests.Should().ContainSingle()
            .Which.Uri!.AbsoluteUri.Should().Be("https://gateway.test/Tickets/roster.csv");
        var invocation = module.VerifyInvoke("saveFile");
        invocation.Arguments[0].Should().Be("roster.csv");
        invocation.Arguments[1].Should().Be("text/csv");
        _toast.Verify(t => t.Warning(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RequiresAuthentication_OnBrowser_WhenTheFetchFails_ShowsTheFailureToast()
    {
        ArrangeClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var module = JSInterop.SetupModule(SaveFileModulePath);
        module.SetupVoid("saveFile", _ => true).SetVoidResult();

        var cut = RenderButton(requiresAuthentication: true);
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        _toast.Verify(t => t.Warning("Could not download the file"), Times.Once);
        module.Invocations.Should().NotContain(i => i.Identifier == "saveFile");
    }

    [Fact]
    public void WithoutRequiresAuthentication_OnBrowser_StillRendersThePlainLink()
    {
        ArrangeClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var cut = RenderButton(requiresAuthentication: false);

        cut.Find("a").GetAttribute("href").Should().Be("https://gateway.test/Tickets/roster.csv");
        _requestsSeen.Should().Be(0, "the anonymous path streams through the browser, not the client");
    }

    [Fact]
    public async Task StagingFolder_OnNativeHead_PurgesTheFolderBeforeWritingTheFile()
    {
        // Per-runtime folder name: both target frameworks run this class at the same time on one machine.
        var folderName = "mmca-download-button-tests-net" + Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var folder = Path.Combine(Path.GetTempPath(), folderName);
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "previous-export.csv");
        await File.WriteAllBytesAsync(stale, "STALE"u8.ToArray(), Xunit.TestContext.Current.CancellationToken);
        try
        {
            ArrangeLinkInterceptingHead();
            ArrangeClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PayloadBytes) });
            string? sharedPath = null;
            var share = new Mock<IShareService>();
            share
                .Setup(x => x.ShareFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((string _, string path, string _, CancellationToken _) => sharedPath = path)
                .ReturnsAsync(true);
            Services.AddSingleton(share.Object);

            var cut = RenderButton(requiresAuthentication: true, stagingFolderName: folderName);
            await cut.Find("button").ClickAsync(new MouseEventArgs());

            sharedPath.Should().Be(Path.Combine(folder, "roster.csv"));
            File.Exists(stale).Should().BeFalse("the staging folder is purged before each write");
            (await File.ReadAllBytesAsync(sharedPath!, Xunit.TestContext.Current.CancellationToken)).Should().Equal(PayloadBytes);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("   ")]
    public async Task StagingFolder_WhenUnusable_WarnsWithoutFetching(string stagingFolderName)
    {
        ArrangeLinkInterceptingHead();
        ArrangeClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PayloadBytes) });

        var cut = RenderButton(requiresAuthentication: false, stagingFolderName: stagingFolderName);
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        _toast.Verify(t => t.Warning("Could not download the file"), Times.Once);
        _requestsSeen.Should().Be(0, "a folder that cannot be staged in must not cost a download");
    }

    private IRenderedComponent<ApiFileDownloadButton> RenderButton(bool requiresAuthentication, string? stagingFolderName = null) =>
        RenderUnderTest<ApiFileDownloadButton>(p => p
            .Add(c => c.RelativeApiPath, "Tickets/roster.csv")
            .Add(c => c.FileName, "roster.csv")
            .Add(c => c.ShareTitle, "Ticket roster")
            .Add(c => c.ContentType, "text/csv")
            .Add(c => c.RequiresAuthentication, requiresAuthentication)
            .Add(c => c.StagingFolderName, stagingFolderName));

    private CapturingHttpMessageHandler ArrangeClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new CapturingHttpMessageHandler(request =>
        {
            _requestsSeen++;
            return respond(request);
        });
        Services.AddSingleton(HttpTestDoubles.ClientFactory(handler));
        return handler;
    }

    private void ArrangeLinkInterceptingHead()
    {
        var externalLink = new Mock<IExternalLinkService>();
        externalLink.SetupGet(x => x.InterceptsLinks).Returns(true);
        Services.AddSingleton(externalLink.Object);
    }
}
