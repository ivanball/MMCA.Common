using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pins the single-flight contract of <see cref="LazyJsModule"/>, the helper the UI services delegate
/// their <c>import()</c> to. An unguarded <c>_module ??= await import(...)</c> lets two concurrent
/// callers each start an import: the browser holds two module instances and the later assignment
/// leaks the earlier reference, which is then never disposed.
/// </summary>
public sealed class LazyJsModuleTests
{
    private const string ModulePath = "./_content/MMCA.Common.UI/probe.js";

    [Fact]
    public async Task ConcurrentCallers_ShareOneImport_AndGetTheSameModule()
    {
        var gate = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = Mock.Of<IJSObjectReference>();
        var js = new Mock<IJSRuntime>();
        js.Setup(r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(gate.Task));

        var sut = new LazyJsModule(js.Object, ModulePath);

        var first = sut.GetOrImportAsync();
        var second = sut.GetOrImportAsync();
        gate.SetResult(module);

        var results = await Task.WhenAll(first, second);

        results[0].Should().BeSameAs(module);
        results[1].Should().BeSameAs(module, "both callers must observe the one imported module");
        js.Verify(
            r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()),
            Times.Once());
    }

    [Fact]
    public async Task AfterTheImportIsCached_NoFurtherImportIsIssued()
    {
        var module = Mock.Of<IJSObjectReference>();
        var js = new Mock<IJSRuntime>();
        js.Setup(r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(module));

        var sut = new LazyJsModule(js.Object, ModulePath);

        (await sut.GetOrImportAsync()).Should().BeSameAs(module);
        (await sut.GetOrImportAsync()).Should().BeSameAs(module);

        js.Verify(
            r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task AFailedImport_IsNotCached_AndTheNextCallRetries()
    {
        // A prerender-time import throws InvalidOperationException. Caching that failed task would
        // leave the module permanently unavailable for the rest of the circuit.
        var module = Mock.Of<IJSObjectReference>();
        var js = new Mock<IJSRuntime>();
        var attempts = 0;
        js.Setup(r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Returns(() =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException<IJSObjectReference>(new InvalidOperationException("JS interop unavailable"))
                    : new ValueTask<IJSObjectReference>(module);
            });

        var sut = new LazyJsModule(js.Object, ModulePath);

        var failing = async () => await sut.GetOrImportAsync();
        await failing.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetOrImportAsync()).Should().BeSameAs(module, "a later call must retry the import");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheModule_AndToleratesADisconnectedCircuit()
    {
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.DisposeAsync()).Throws(new JSDisconnectedException("circuit gone"));
        var js = new Mock<IJSRuntime>();
        js.Setup(r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(module.Object));

        var sut = new LazyJsModule(js.Object, ModulePath);
        await sut.GetOrImportAsync();

        var dispose = async () => await sut.DisposeAsync();

        await dispose.Should().NotThrowAsync();
        module.Verify(m => m.DisposeAsync(), Times.Once());
    }

    [Fact]
    public async Task DisposeAsync_WithoutAnImport_DoesNothing()
    {
        var js = new Mock<IJSRuntime>();
        var sut = new LazyJsModule(js.Object, ModulePath);

        await sut.DisposeAsync();

        js.VerifyNoOtherCalls();
    }
}
