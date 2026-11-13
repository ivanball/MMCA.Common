using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Layout;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.Services.Offline;
using MMCA.Common.UI.Tests.Services.Offline;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Pins <see cref="OfflineWriteNotifier"/> (item 38, rules 17, 17a, 18): the markup-free component
/// that drains writes left by an earlier session on its first interactive render and turns each
/// settled offline write into a toast. With no queue registered it is inert, which is what lets
/// Common's <see cref="MainLayout"/> host it unconditionally.
/// </summary>
public sealed class OfflineWriteNotifierTests : BunitTestBase
{
    private const string ConflictText = "A change you made offline could not be applied because the item changed. Reload and try again.";
    private const string RejectedText = "A change you made offline was refused by the server.";
    private const string ExpiredText = "A change you made offline was too old to send and was discarded.";
    private const string SyncedText = "Your offline changes were saved.";

    private readonly Mock<IOfflineWriteQueue> _queue = new();
    private readonly Mock<IToastService> _toast = new();
    private readonly ControllableConnectivityService _connectivity = new();

    public OfflineWriteNotifierTests()
    {
        _queue.SetupGet(q => q.IsAvailable).Returns(true);
        _queue.Setup(q => q.ReplayAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        // Last registration wins over the base's Mud-backed toast facade and always-online default.
        Services.AddSingleton(_toast.Object);
        Services.AddSingleton<IConnectivityStatusService>(_connectivity);
    }

    // Rule 17: resolution, replay on first interactive render, no markup
    [Fact]
    public void NoQueueRegistered_RendersNothing_NoSubscription()
    {
        var cut = RenderNotifier(registerQueue: false);

        cut.Markup.Trim().Should().BeEmpty();
        _connectivity.SubscriberCount.Should().Be(0, "with no queue there is nothing to listen to");
        _queue.Invocations.Should().BeEmpty();
        _toast.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void FirstInteractiveRender_Online_CallsReplay()
    {
        var cut = RenderNotifier();

        cut.WaitForAssertion(() => _queue.Verify(q => q.ReplayAsync(It.IsAny<CancellationToken>()), Times.Once()));
        cut.Markup.Trim().Should().BeEmpty("the notifier renders no markup");
    }

    [Fact]
    public void FirstInteractiveRender_Offline_DoesNotReplay()
    {
        _connectivity.SetOnline(false);

        var cut = RenderNotifier();

        _queue.Verify(q => q.ReplayAsync(It.IsAny<CancellationToken>()), Times.Never());
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void LaterRenders_DoNotReplayAgain()
    {
        var cut = RenderNotifier();
        cut.WaitForAssertion(() => _queue.Verify(q => q.ReplayAsync(It.IsAny<CancellationToken>()), Times.Once()));

        cut.Render(p => p.Add(c => c.ShowCompleted, true));

        _queue.Verify(q => q.ReplayAsync(It.IsAny<CancellationToken>()), Times.Once(), "only the first interactive render drains");
    }

    [Fact]
    public void Prerender_DoesNotReplay()
    {
        var cut = RenderNotifier(interactive: false);

        _queue.Verify(q => q.ReplayAsync(It.IsAny<CancellationToken>()), Times.Never());
        _queue.VerifyAdd(q => q.WriteSettled += It.IsAny<EventHandler<OfflineWriteSettledEventArgs>>(), Times.Never());
        cut.Markup.Trim().Should().BeEmpty();
    }

    // Rule 17a: hosted by Common's MainLayout
    [Fact]
    public void MainLayout_RendersTheNotifierDirectlyAfterTheOfflineBanner()
    {
        Services.AddSingleton(new Mock<IAuthUIService>().Object);
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var cut = RenderUnderTest<MainLayout>(p => p.Add(layout => layout.Body, "<p>Page body</p>"));

        cut.FindComponents<OfflineWriteNotifier>().Should().ContainSingle(
            "an app that registers no queue still gets the inert notifier, with no layout change");

        // Render-tree order: the online OfflineBanner renders no child component, so the next
        // component in the tree is the notifier when it sits directly after the banner.
        var components = cut.FindComponents<IComponent>().Select(c => c.Instance).ToList();
        var banner = components.FindIndex(c => c is OfflineBanner);
        var notifier = components.FindIndex(c => c is OfflineWriteNotifier);
        banner.Should().BeGreaterThanOrEqualTo(0);
        notifier.Should().Be(banner + 1, "the notifier is rendered directly after OfflineBanner");
    }

    // Rule 18: toasts
    [Fact]
    public void Conflict_ToastsWarningDefaultText()
    {
        var cut = RenderSubscribed();

        Raise(OfflineWriteOutcome.Conflict, Result.Failure(Error.Conflict("Concurrency.PreconditionFailed", "Changed.")));

        cut.WaitForAssertion(() => _toast.Verify(t => t.Warning(ConflictText), Times.Once()));
        _toast.Verify(t => t.Error(It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public void Rejected_ToastsError()
    {
        var cut = RenderSubscribed();

        Raise(OfflineWriteOutcome.Rejected, Result.Failure(Error.Validation("Widget.Invalid", "Invalid.")));

        cut.WaitForAssertion(() => _toast.Verify(t => t.Error(RejectedText), Times.Once()));
    }

    [Fact]
    public void Expired_ToastsWarning()
    {
        var cut = RenderSubscribed();

        Raise(OfflineWriteOutcome.Expired, null);

        cut.WaitForAssertion(() => _toast.Verify(t => t.Warning(ExpiredText), Times.Once()));
    }

    [Fact]
    public void Completed_NoToastByDefault_ToastWhenShowCompleted()
    {
        var cut = RenderSubscribed();

        Raise(OfflineWriteOutcome.Completed, Result.Success());
        // A later conflict proves the completed event was already handled when the toast is checked.
        Raise(OfflineWriteOutcome.Conflict, Result.Failure(Error.Conflict("Concurrency.PreconditionFailed", "Changed.")));
        cut.WaitForAssertion(() => _toast.Verify(t => t.Warning(ConflictText), Times.Once()));
        _toast.Verify(t => t.Success(It.IsAny<string>()), Times.Never(), "a completed write is silent by default");

        cut.Render(p => p.Add(c => c.ShowCompleted, true));
        Raise(OfflineWriteOutcome.Completed, Result.Success());

        cut.WaitForAssertion(() => _toast.Verify(t => t.Success(SyncedText), Times.Once()));
    }

    [Fact]
    public void Describe_OverridesText()
    {
        var cut = RenderSubscribed(p => p.Add(
            c => c.Describe,
            args => args.Outcome == OfflineWriteOutcome.Conflict ? "Widget 5 changed somewhere else." : null));

        Raise(OfflineWriteOutcome.Conflict, Result.Failure(Error.Conflict("Concurrency.PreconditionFailed", "Changed.")));
        Raise(OfflineWriteOutcome.Rejected, Result.Failure(Error.Validation("Widget.Invalid", "Invalid.")));

        cut.WaitForAssertion(() =>
        {
            _toast.Verify(t => t.Warning("Widget 5 changed somewhere else."), Times.Once());
            _toast.Verify(t => t.Error(RejectedText), Times.Once(), "a null Describe result falls back to the default text");
        });
        _toast.Verify(t => t.Warning(ConflictText), Times.Never());
    }

    [Fact]
    public async Task Dispose_Unsubscribes()
    {
        RenderSubscribed();

        await DisposeComponentsAsync();

        _queue.VerifyRemove(q => q.WriteSettled -= It.IsAny<EventHandler<OfflineWriteSettledEventArgs>>(), Times.AtLeastOnce());
        Raise(OfflineWriteOutcome.Conflict, Result.Failure(Error.Conflict("Concurrency.PreconditionFailed", "Changed.")));
        _toast.Verify(t => t.Warning(It.IsAny<string>()), Times.Never());
    }

    private static PendingWrite Write() => new()
    {
        Id = Guid.NewGuid(),
        Kind = "Tests.Widget.Create.v1",
        IdempotencyKey = "client-key-1",
        PayloadJson = "{}",
        UserScope = "7",
        EnqueuedOn = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero),
    };

    private void Raise(OfflineWriteOutcome outcome, Result? result) =>
        _queue.Raise(q => q.WriteSettled += null, new OfflineWriteSettledEventArgs(Write(), outcome, result));

    private IRenderedComponent<OfflineWriteNotifier> RenderSubscribed(
        Action<ComponentParameterCollectionBuilder<OfflineWriteNotifier>>? configure = null)
    {
        var cut = RenderNotifier(configure: configure);
        cut.WaitForAssertion(() => _queue.VerifyAdd(
            q => q.WriteSettled += It.IsAny<EventHandler<OfflineWriteSettledEventArgs>>(),
            Times.AtLeastOnce()));
        return cut;
    }

    private IRenderedComponent<OfflineWriteNotifier> RenderNotifier(
        bool registerQueue = true,
        bool interactive = true,
        Action<ComponentParameterCollectionBuilder<OfflineWriteNotifier>>? configure = null)
    {
        if (registerQueue)
        {
            Services.AddSingleton(_queue.Object);
        }

        // After every Services.Add call: SetRendererInfo freezes the service provider.
        SetRendererInfo(new RendererInfo(interactive ? "WebAssembly" : "Static", interactive));
        return RenderUnderTest<OfflineWriteNotifier>(p => configure?.Invoke(p));
    }
}
