using AwesomeAssertions;
using Bunit;
using Microsoft.JSInterop;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// <see cref="PendingToastService"/> parks one toast in <c>sessionStorage</c> under a fixed key so
/// it survives a <c>forceLoad</c> navigation. These pin the key and payload it writes, and that a
/// storage failure never reaches the caller (a lost confirmation toast must not fail the action).
/// </summary>
public sealed class PendingToastServiceTests : BunitTestBase
{
    private const string NavInteropModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

    [Fact]
    public async Task QueueAsync_WritesThePayloadUnderTheFixedKey()
    {
        var module = JSInterop.SetupModule(NavInteropModulePath);
        module.SetupVoid("sessionSet", _ => true).SetVoidResult();
        await using var sut = new PendingToastService(JSInterop.JSRuntime);

        await sut.QueueAsync(ToastSeverity.Success, "Account deleted", Xunit.TestContext.Current.CancellationToken);

        var write = module.Invocations["sessionSet"].Should().ContainSingle().Subject;
        write.Arguments[0].Should().Be(PendingToastService.StorageKey);
        write.Arguments[1].Should().Be(new PendingToastService.PendingToast(ToastSeverity.Success, "Account deleted"));
    }

    [Fact]
    public async Task QueueAsync_WithABlankMessage_WritesNothing()
    {
        var module = JSInterop.SetupModule(NavInteropModulePath);
        module.SetupVoid("sessionSet", _ => true).SetVoidResult();
        await using var sut = new PendingToastService(JSInterop.JSRuntime);

        await sut.QueueAsync(ToastSeverity.Success, "  ", Xunit.TestContext.Current.CancellationToken);

        module.Invocations["sessionSet"].Should().BeEmpty();
    }

    [Fact]
    public async Task QueueAsync_WhenStorageThrows_IsSwallowed()
    {
        JSInterop.SetupModule(NavInteropModulePath)
            .SetupVoid("sessionSet", _ => true)
            .SetException(new JSException("QuotaExceededError"));
        await using var sut = new PendingToastService(JSInterop.JSRuntime);

        var act = async () => await sut.QueueAsync(ToastSeverity.Success, "Account deleted", Xunit.TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TakeAsync_WhenTheCircuitIsGone_ReturnsNothing()
    {
        JSInterop.SetupModule(NavInteropModulePath)
            .Setup<PendingToastService.PendingToast?>("sessionGet", PendingToastService.StorageKey)
            .SetException(new JSDisconnectedException("circuit closed"));
        await using var sut = new PendingToastService(JSInterop.JSRuntime);

        (await sut.TakeAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
