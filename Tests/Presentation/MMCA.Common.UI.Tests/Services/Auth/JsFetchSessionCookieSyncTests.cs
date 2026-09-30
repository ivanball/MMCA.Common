using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="JsFetchSessionCookieSync"/> reports the cookie write and clear outcome (M129):
/// the script's own 2xx verdict is returned, and an unavailable JS interop reports false rather than
/// passing for a written cookie.
/// </summary>
public sealed class JsFetchSessionCookieSyncTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncAsync_ReturnsWhatTheScriptReported(bool scriptResult)
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<bool>("mmcaAuthCookie.set", It.IsAny<object?[]?>()))
            .ReturnsAsync(scriptResult);
        var sut = new JsFetchSessionCookieSync(js.Object);

        var written = await sut.SyncAsync("access-token", "refresh-token");

        written.Should().Be(scriptResult);
    }

    [Fact]
    public async Task SyncAsync_WhenTheCircuitIsGone_ReturnsFalse()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<bool>("mmcaAuthCookie.set", It.IsAny<object?[]?>()))
            .ThrowsAsync(new JSDisconnectedException("gone"));
        var sut = new JsFetchSessionCookieSync(js.Object);

        var written = await sut.SyncAsync("access-token", "refresh-token");

        written.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClearAsync_ReturnsWhatTheScriptReported(bool scriptResult)
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<bool>("mmcaAuthCookie.clear", It.IsAny<object?[]?>()))
            .ReturnsAsync(scriptResult);
        var sut = new JsFetchSessionCookieSync(js.Object);

        var cleared = await sut.ClearAsync();

        cleared.Should().Be(scriptResult);
    }

    [Fact]
    public async Task ClearAsync_WhenInteropIsUnavailable_ReturnsFalse()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<bool>("mmcaAuthCookie.clear", It.IsAny<object?[]?>()))
            .ThrowsAsync(new InvalidOperationException("prerendering"));
        var sut = new JsFetchSessionCookieSync(js.Object);

        var cleared = await sut.ClearAsync();

        cleared.Should().BeFalse();
    }
}
