using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Diagnostics;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.Services.Diagnostics;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Diagnostics;

/// <summary>
/// bUnit tests for <see cref="InAppDiagnosticsLog"/>: it renders the buffered entries newest last,
/// shares the log as text through the native share sheet, and falls back to the clipboard where no
/// share surface exists.
/// </summary>
public sealed class InAppDiagnosticsLogTests : BunitTestBase
{
    private readonly InAppLogBuffer _buffer = new(Options.Create(new InAppDiagnosticsOptions()));
    private readonly Mock<IShareService> _share = new();
    private readonly Mock<IClipboardService> _clipboard = new();
    private readonly Mock<IToastService> _toast = new();

    public InAppDiagnosticsLogTests()
    {
        Services.AddSingleton(_buffer);
        Services.AddSingleton(_share.Object);
        Services.AddSingleton(_clipboard.Object);
        Services.AddSingleton(_toast.Object);
    }

    [Fact]
    public void RendersEachEntry_NewestLast()
    {
        _buffer.Add(Entry(LogLevel.Information, "Started sync"));
        _buffer.Add(Entry(LogLevel.Warning, "Retrying"));
        _buffer.Add(Entry(LogLevel.Error, "Sync failed"));

        var cut = RenderLog();

        var items = cut.FindAll("[data-testid=diagnostics-entries] li");
        items.Should().HaveCount(3);
        items[0].TextContent.Should().Contain("[Information] App.Sync: Started sync");
        items[2].TextContent.Should().Contain("[Error] App.Sync: Sync failed");
        items[2].ClassList.Should().Contain("mud-error-text");
    }

    [Fact]
    public void WithNoEntries_ShowsTheEmptyState()
    {
        var cut = RenderLog();

        cut.FindAll("[data-testid=diagnostics-empty]").Should().ContainSingle();
        cut.FindAll("[data-testid=diagnostics-entries]").Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_ShowsEntriesWrittenAfterTheFirstRender()
    {
        var cut = RenderLog();
        _buffer.Add(Entry(LogLevel.Information, "Later"));

        await cut.Find("[data-testid=diagnostics-refresh]").ClickAsync(new MouseEventArgs());

        cut.FindAll("[data-testid=diagnostics-entries] li").Should().ContainSingle();
    }

    [Fact]
    public async Task Share_HandsTheLogTextToTheShareSheet()
    {
        _buffer.Add(Entry(LogLevel.Information, "Started sync"));
        _share
            .Setup(x => x.ShareTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = RenderLog();
        await cut.Find("[data-testid=diagnostics-share]").ClickAsync(new MouseEventArgs());

        _share.Verify(
            x => x.ShareTextAsync("Diagnostics log", _buffer.ToText(), It.IsAny<CancellationToken>()),
            Times.Once);
        _clipboard.Verify(
            x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Share_WhenUnavailable_CopiesTheLogInstead()
    {
        _buffer.Add(Entry(LogLevel.Information, "Started sync"));
        _share
            .Setup(x => x.ShareTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _clipboard
            .Setup(x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = RenderLog();
        await cut.Find("[data-testid=diagnostics-share]").ClickAsync(new MouseEventArgs());

        _clipboard.Verify(x => x.SetTextAsync(_buffer.ToText(), It.IsAny<CancellationToken>()), Times.Once);
        _toast.Verify(x => x.Success("Log copied to clipboard"), Times.Once);
    }

    [Fact]
    public async Task Copy_CopiesTheLogText()
    {
        _buffer.Add(Entry(LogLevel.Information, "Started sync"));
        _clipboard
            .Setup(x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = RenderLog();
        await cut.Find("[data-testid=diagnostics-copy]").ClickAsync(new MouseEventArgs());

        _clipboard.Verify(x => x.SetTextAsync(_buffer.ToText(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clear_EmptiesTheBufferAndTheList()
    {
        _buffer.Add(Entry(LogLevel.Information, "Started sync"));

        var cut = RenderLog();
        await cut.Find("[data-testid=diagnostics-clear]").ClickAsync(new MouseEventArgs());

        _buffer.Count.Should().Be(0);
        cut.FindAll("[data-testid=diagnostics-empty]").Should().ContainSingle();
    }

    private IRenderedComponent<InAppDiagnosticsLog> RenderLog() =>
        RenderUnderTest<InAppDiagnosticsLog>(_ => { });

    private static InAppLogEntry Entry(LogLevel level, string message) =>
        new(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero), level, "App.Sync", message, null);
}
