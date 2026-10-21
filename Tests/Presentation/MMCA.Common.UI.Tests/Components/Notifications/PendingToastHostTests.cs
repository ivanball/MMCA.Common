using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Notifications;
using MMCA.Common.UI.Services;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Notifications;

/// <summary>
/// <see cref="PendingToastHost"/> sits in the shared layout and shows, once, the toast a page parked
/// with <see cref="IPendingToastService"/> before a <c>forceLoad</c> navigation. It runs over the
/// real <see cref="PendingToastService"/> (registered by the shared harness) so the storage key and
/// the read-then-remove order are exercised end to end; only the toast renderer is a double.
/// </summary>
public sealed class PendingToastHostTests : BunitTestBase
{
    private const string NavInteropModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

    private readonly Mock<IToastService> _toast = new();

    public PendingToastHostTests() =>

        // Registered after the harness's Mud-backed facade, so this one wins.
        Services.AddSingleton(_toast.Object);

    [Fact]
    public void WithAParkedToast_ShowsItExactlyOnceAndRemovesTheKey()
    {
        var module = JSInterop.SetupModule(NavInteropModulePath);
        module
            .Setup<PendingToastService.PendingToast?>("sessionGet", PendingToastService.StorageKey)
            .SetResult(new PendingToastService.PendingToast(ToastSeverity.Success, "Account deleted"));
        module.SetupVoid("sessionRemove", PendingToastService.StorageKey).SetVoidResult();

        var cut = Render<PendingToastHost>();
        cut.WaitForAssertion(() => module.Invocations["sessionRemove"].Should().ContainSingle());

        // A later render of the same host (the layout re-renders on every navigation) must not
        // read storage or show the toast again.
        cut.Render();

        _toast.Verify(t => t.Show("Account deleted", ToastSeverity.Success), Times.Once);
        _toast.VerifyNoOtherCalls();
        module.Invocations["sessionGet"].Should().ContainSingle();
        module.Invocations["sessionRemove"].Single().Arguments[0].Should().Be(PendingToastService.StorageKey);
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void WithNothingParked_DoesNothing()
    {
        var module = JSInterop.SetupModule(NavInteropModulePath);
        module
            .Setup<PendingToastService.PendingToast?>("sessionGet", PendingToastService.StorageKey)
            .SetResult(null);

        var cut = Render<PendingToastHost>();
        cut.WaitForAssertion(() => module.Invocations["sessionGet"].Should().ContainSingle());

        _toast.VerifyNoOtherCalls();
        module.Invocations["sessionRemove"].Should().BeEmpty();
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void WhenStorageThrows_TheFailureIsSwallowed()
    {
        var module = JSInterop.SetupModule(NavInteropModulePath);
        module
            .Setup<PendingToastService.PendingToast?>("sessionGet", PendingToastService.StorageKey)
            .SetException(new JSException("SecurityError: sessionStorage is disabled"));

        var render = () => Render<PendingToastHost>();

        var cut = render.Should().NotThrow().Subject;
        cut.WaitForAssertion(() => module.Invocations["sessionGet"].Should().ContainSingle());
        _toast.VerifyNoOtherCalls();
    }
}
