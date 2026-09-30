using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Interop;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Interop;

/// <summary>
/// Verifies <see cref="BrowserExternalLinkService"/> passes the script's verdict through (L70), and
/// reports false when JS interop is unavailable (prerender, a torn-down circuit) instead of
/// claiming an open that never happened.
/// </summary>
public sealed class BrowserExternalLinkServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenAsync_ReturnsWhatTheScriptReported(bool scriptResult)
    {
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.InvokeAsync<bool?>("openExternal", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(scriptResult);
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(module.Object);
        await using var capabilities = new CapabilitiesJsModule(js.Object);
        var sut = new BrowserExternalLinkService(capabilities);

        var opened = await sut.OpenAsync(new Uri("https://example.com/"), TestContext.Current.CancellationToken);

        opened.Should().Be(scriptResult);
    }

    [Fact]
    public async Task OpenAsync_WhenInteropIsUnavailable_ReturnsFalse()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(new InvalidOperationException("prerendering"));
        await using var capabilities = new CapabilitiesJsModule(js.Object);
        var sut = new BrowserExternalLinkService(capabilities);

        var opened = await sut.OpenAsync(new Uri("https://example.com/"), TestContext.Current.CancellationToken);

        opened.Should().BeFalse();
    }
}
