using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Navigation;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Navigation;

/// <summary>
/// Verifies <see cref="MauiBackNavigationBridge"/> releases the JS module it imports on every back
/// press (L68): the class is static, so an undisposed import leaked one JS object reference per press.
/// </summary>
public sealed class MauiBackNavigationBridgeTests
{
    [Fact]
    public async Task HandleBackPressedAsync_DisposesTheImportedModule()
    {
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.InvokeAsync<BackNavigationResult>("tryGoBack", It.IsAny<object?[]?>()))
            .ReturnsAsync(new BackNavigationResult(Handled: true, AtRoot: false));
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .ReturnsAsync(module.Object);

        var result = await MauiBackNavigationBridge.HandleBackPressedAsync(js.Object);

        result.Should().Be(new BackNavigationResult(Handled: true, AtRoot: false));
        module.Verify(m => m.DisposeAsync(), Times.Once());
    }
}
