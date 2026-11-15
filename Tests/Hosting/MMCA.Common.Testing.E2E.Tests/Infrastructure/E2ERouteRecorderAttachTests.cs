using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Moq;

namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>
/// The recorder's Playwright wiring, driven through mocked <see cref="IPage"/> and
/// <see cref="IBrowserContext"/> events instead of a browser: only main-frame navigations and
/// non-navigation requests are recorded (so a page load is not logged twice), closing the page or the
/// context flushes the log exactly once, and with <c>E2E_ROUTE_LOG_DIR</c> unset nothing subscribes.
/// </summary>
[Collection(E2EEnvironmentCollection.Name)]
public sealed class E2ERouteRecorderAttachTests : IDisposable
{
    private const string VariableName = "E2E_ROUTE_LOG_DIR";

    private readonly string _directory = Directory.CreateTempSubdirectory("mmca-routelog-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable(VariableName);
    private readonly Mock<IPage> _page = new();
    private readonly Mock<IBrowserContext> _context = new();

    /// <summary>A child frame's navigation is an embed, not a page the test opened.</summary>
    [Fact]
    public void MainFrameNavigation_IsRecorded_AndAChildFrameIsNot()
    {
        var recorder = new E2ERouteRecorder(_directory, "Sessions.Detail", [new Uri("https://ui.test")]);
        recorder.Attach(_page.Object, _context.Object);

        _page.Raise(p => p.FrameNavigated += null, _page.Object, Frame("https://ui.test/sessions/5?from=list", parent: null));
        _page.Raise(p => p.FrameNavigated += null, _page.Object, Frame("https://ui.test/embedded-video", parent: Frame("https://ui.test/sessions/5", parent: null)));
        _page.Raise(p => p.Close += null, _page.Object, _page.Object);

        var content = SingleLogContent();
        content.Should().Contain("\"kind\":\"navigation\"").And.Contain("\"path\":\"/sessions/5\"")
            .And.NotContain("embedded-video", "only the main frame's navigations are pages the test reached")
            .And.NotContain("from=list");
    }

    /// <summary>A navigation request is already logged as a navigation; only API calls come from requests.</summary>
    [Fact]
    public void ApiRequest_IsRecorded_AndANavigationRequestIsNot()
    {
        var recorder = new E2ERouteRecorder(_directory, "Orders.Place", [new Uri("https://ui.test")]);
        recorder.Attach(_page.Object, _context.Object);

        _context.Raise(c => c.Request += null, _context.Object, Request("https://ui.test/checkout", "GET", isNavigation: true));
        _context.Raise(c => c.Request += null, _context.Object, Request("https://ui.test/api/orders", "POST", isNavigation: false));
        _context.Raise(c => c.Close += null, _context.Object, _context.Object);

        var content = SingleLogContent();
        content.Should().Contain("\"method\":\"POST\"").And.Contain("\"path\":\"/api/orders\"")
            .And.NotContain("/checkout", "the page load is the navigation event's to record");
    }

    /// <summary>The page closes, then the context: the second close has nothing left to write.</summary>
    [Fact]
    public void PageThenContextClose_WritesOneFile()
    {
        var recorder = new E2ERouteRecorder(_directory, "Home.Renders", [new Uri("https://ui.test")]);
        recorder.Attach(_page.Object, _context.Object);

        _page.Raise(p => p.FrameNavigated += null, _page.Object, Frame("https://ui.test/", parent: null));
        _page.Raise(p => p.Close += null, _page.Object, _page.Object);
        _context.Raise(c => c.Close += null, _context.Object, _context.Object);

        Directory.GetFiles(_directory).Should().ContainSingle("the queue is drained by the first close");
    }

    /// <summary>Off by default: no recorder and no event subscription at all.</summary>
    [Fact]
    public void AttachIfEnabled_WithTheVariableUnset_SubscribesNothing()
    {
        Environment.SetEnvironmentVariable(VariableName, null);

        var recorder = E2ERouteRecorder.AttachIfEnabled("https://ui.test", _page.Object, _context.Object);

        recorder.Should().BeNull();
        _page.VerifyAdd(p => p.FrameNavigated += It.IsAny<EventHandler<IFrame>>(), Times.Never());
        _context.VerifyAdd(c => c.Request += It.IsAny<EventHandler<IRequest>>(), Times.Never());
    }

    /// <summary>A malformed base URL must never fail the test it is only observing.</summary>
    [Fact]
    public void AttachIfEnabled_WithAnUnparsableBaseUrl_AttachesNothing()
    {
        Environment.SetEnvironmentVariable(VariableName, _directory);

        var recorder = E2ERouteRecorder.AttachIfEnabled("not a url", _page.Object, _context.Object);

        recorder.Should().BeNull();
        _page.VerifyAdd(p => p.FrameNavigated += It.IsAny<EventHandler<IFrame>>(), Times.Never());
    }

    /// <summary>With the variable set, the recorder subscribes to the page and the context.</summary>
    [Fact]
    public void AttachIfEnabled_WithTheVariableSet_Subscribes()
    {
        Environment.SetEnvironmentVariable(VariableName, _directory);

        var recorder = E2ERouteRecorder.AttachIfEnabled("https://ui.test", _page.Object, _context.Object);

        recorder.Should().NotBeNull();
        _page.VerifyAdd(p => p.FrameNavigated += It.IsAny<EventHandler<IFrame>>(), Times.Once());
        _context.VerifyAdd(c => c.Request += It.IsAny<EventHandler<IRequest>>(), Times.Once());
    }

    /// <summary>Restores the variable and removes the temp folder.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VariableName, _previous);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing on a slow file system must not fail a test that already passed.
        }
    }

    private static IFrame Frame(string url, IFrame? parent)
    {
        var frame = new Mock<IFrame>();
        frame.SetupGet(f => f.Url).Returns(url);
        frame.SetupGet(f => f.ParentFrame).Returns(parent);
        return frame.Object;
    }

    private static IRequest Request(string url, string method, bool isNavigation)
    {
        var request = new Mock<IRequest>();
        request.SetupGet(r => r.Url).Returns(url);
        request.SetupGet(r => r.Method).Returns(method);
        request.SetupGet(r => r.IsNavigationRequest).Returns(isNavigation);
        return request.Object;
    }

    private string SingleLogContent() => File.ReadAllText(Directory.GetFiles(_directory).Single());
}
