using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Capabilities.DeviceStatus;

/// <summary>
/// Verifies <see cref="BrowserConnectivityStatusService"/> (ADR-042): it reports online until the
/// script answers, adopts and announces the browser's status once watching starts, retries on a later
/// call while JS is unavailable (prerender), starts watching only once, and stops the browser
/// listeners on dispose only when it started them.
/// </summary>
public sealed class BrowserConnectivityStatusServiceTests : IAsyncDisposable
{
    private readonly Mock<IJSObjectReference> _module = new();
    private readonly CapabilitiesJsModule _capabilities;

    public BrowserConnectivityStatusServiceTests()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(_module.Object);
        _capabilities = new CapabilitiesJsModule(js.Object);
    }

    public ValueTask DisposeAsync() => _capabilities.DisposeAsync();

    [Fact]
    public void BeforeInitialize_ReportsOnline()
    {
        var sut = new BrowserConnectivityStatusService(_capabilities);

        sut.IsOnline.Should().BeTrue();
    }

    [Fact]
    public async Task Initialize_WhenTheBrowserIsOffline_AdoptsItAndAnnouncesTheChange()
    {
        object?[]? sent = null;
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .Callback<string, CancellationToken, object?[]?>((_, _, args) => sent = args)
            .ReturnsAsync(false);
        await using var sut = new BrowserConnectivityStatusService(_capabilities);
        var changes = 0;
        sut.ConnectivityChanged += (_, _) => changes++;

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        sut.IsOnline.Should().BeFalse();
        changes.Should().Be(1);
        sent.Should().ContainSingle().Which.Should().BeOfType<DotNetObjectReference<BrowserConnectivityStatusService>>()
            .Which.Value.Should().BeSameAs(sut, "the script calls back into this instance on online/offline");
    }

    [Fact]
    public async Task Initialize_WhenTheBrowserIsOnline_RaisesNoChange()
    {
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(true);
        await using var sut = new BrowserConnectivityStatusService(_capabilities);
        var changes = 0;
        sut.ConnectivityChanged += (_, _) => changes++;

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        sut.IsOnline.Should().BeTrue();
        changes.Should().Be(0);
    }

    [Fact]
    public async Task Initialize_WhileJsIsUnavailable_StaysOnline_AndRetriesOnTheNextCall_WithTheSameCallbackTarget()
    {
        var sent = new List<object?>();
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .Returns<string, CancellationToken, object?[]?>((_, _, args) =>
            {
                sent.Add(args![0]);
                return ValueTask.FromResult<bool?>(sent.Count == 1 ? null : false);
            });
        await using var sut = new BrowserConnectivityStatusService(_capabilities);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        sut.IsOnline.Should().BeTrue("nothing is known before the script answers");

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        sut.IsOnline.Should().BeFalse("the retry started watching");
        sent.Should().HaveCount(2);
        sent[1].Should().BeSameAs(sent[0], "the callback reference is created once and reused by the retry");
    }

    [Fact]
    public async Task Initialize_OnceWatching_DoesNotStartASecondWatch()
    {
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(false);
        await using var sut = new BrowserConnectivityStatusService(_capabilities);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        sut.OnBrowserConnectivityChanged(isOnline: true);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        sut.IsOnline.Should().BeTrue("the second call must not re-read and overwrite the live status");
        _module.Verify(
            m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()),
            Times.Once());
    }

    [Fact]
    public async Task BrowserCallback_AnnouncesOnlyRealTransitions()
    {
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(true);
        await using var sut = new BrowserConnectivityStatusService(_capabilities);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        var changes = 0;
        sut.ConnectivityChanged += (_, _) => changes++;

        sut.OnBrowserConnectivityChanged(isOnline: true);
        sut.OnBrowserConnectivityChanged(isOnline: false);
        sut.OnBrowserConnectivityChanged(isOnline: false);

        sut.IsOnline.Should().BeFalse();
        changes.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_AfterWatching_StopsTheBrowserListeners()
    {
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(true);
        var sut = new BrowserConnectivityStatusService(_capabilities);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        await sut.DisposeAsync();

        _module.Verify(
            m => m.InvokeAsync<bool?>("unwatchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()),
            Times.Once());
    }

    [Fact]
    public async Task Dispose_WithoutWatching_CallsNoScript()
    {
        _module.Setup(m => m.InvokeAsync<bool?>("watchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync((bool?)null);
        var sut = new BrowserConnectivityStatusService(_capabilities);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        await sut.DisposeAsync();

        _module.Verify(
            m => m.InvokeAsync<bool?>("unwatchOnline", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()),
            Times.Never());
    }
}
