using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Services;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Pinned toasts never expire, and MudBlazor displays at most
/// <see cref="SnackbarConfiguration.MaxDisplayedSnackbars"/> at once, queueing the rest out of
/// sight. These tests run <see cref="MudToastService"/> over MudBlazor's real
/// <see cref="SnackbarService"/> (capped at 3, as the ADC hosts configure it) and assert that a new
/// pinned toast is always among the displayed ones. The real service decides what is "displayed",
/// so the assertions read <see cref="ISnackbar.ShownSnackbars"/> exactly as the provider does.
/// </summary>
public sealed class MudToastServicePinnedCapTests : IDisposable
{
    private const int Cap = 3;

    private readonly RecordingSnackbarService _snackbar = new();

    private MudToastService Toast { get; }

    public MudToastServicePinnedCapTests() => Toast = new MudToastService(_snackbar);

    public void Dispose() => _snackbar.Dispose();

    [Fact]
    public void ShowPersistent_FourInARow_RemovesTheOldestSoTheNewestThreeAreDisplayed()
    {
        var first = ShowPersistentAndCapture("One");
        var second = ShowPersistentAndCapture("Two");
        var third = ShowPersistentAndCapture("Three");

        Toast.ShowPersistent("Four", "body");

        var shown = _snackbar.ShownSnackbars.ToList();
        shown.Should().HaveCount(Cap);
        shown[0].Should().BeSameAs(second);
        shown[1].Should().BeSameAs(third);
        shown.Should().NotContain(first);
        _snackbar.RemovedByToastService.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void ShowActionRequiringInteraction_FourInARow_KeepsTheNewestThreeDisplayed()
    {
        Toast.ShowAction("Poll 1 opened", "Vote now", () => Task.CompletedTask, requireInteraction: true);
        Toast.ShowAction("Poll 2 opened", "Vote now", () => Task.CompletedTask, requireInteraction: true);
        Toast.ShowAction("Poll 3 opened", "Vote now", () => Task.CompletedTask, requireInteraction: true);
        Toast.ShowAction("Poll 4 opened", "Vote now", () => Task.CompletedTask, requireInteraction: true);

        _snackbar.ShownSnackbars.Select(s => s.Message).Should()
            .Equal("Poll 2 opened", "Poll 3 opened", "Poll 4 opened");
    }

    [Fact]
    public void NonPinnedToast_AfterThreePinned_QueuesAsBeforeAndRemovesNoPinnedToast()
    {
        var first = ShowPersistentAndCapture("One");
        var second = ShowPersistentAndCapture("Two");
        var third = ShowPersistentAndCapture("Three");

        Toast.Info("Saved");

        // Today's behavior for a timed toast: it waits in MudBlazor's queue behind the three
        // displayed ones; nothing is evicted to make room for it.
        _snackbar.ShownSnackbars.Should().Equal(first, second, third);
        _snackbar.RemovedByToastService.Should().BeEmpty();
    }

    [Fact]
    public void PinnedToastTheUserClosed_IsNotCounted()
    {
        var first = ShowPersistentAndCapture("One");
        ShowPersistentAndCapture("Two");

        // The close button ends in Snackbar.OnClose, which is what ForceClose raises synchronously.
        first.ForceClose();

        Toast.ShowPersistent("Three", "body");
        Toast.ShowPersistent("Four", "body");

        _snackbar.ShownSnackbars.Should().HaveCount(Cap).And.NotContain(first);
        _snackbar.RemovedByToastService.Should().BeEmpty("two open pinned toasts are below the cap of three");
    }

    [Fact]
    public void PinnedToastRemovedOutsideTheService_IsNotCounted()
    {
        var first = ShowPersistentAndCapture("One");
        var second = ShowPersistentAndCapture("Two");

        // Removal through ISnackbar raises no close event; the service must still stop counting it.
        _snackbar.Clear();

        ShowPersistentAndCapture("Three");
        ShowPersistentAndCapture("Four");
        Toast.ShowPersistent("Five", "body");

        _snackbar.ShownSnackbars.Should().HaveCount(Cap).And.NotContain(first).And.NotContain(second);
        _snackbar.RemovedByToastService.Should().BeEmpty("Three, Four and Five are the only open pinned toasts");
    }

    private Snackbar ShowPersistentAndCapture(string title)
    {
        var before = _snackbar.ShownSnackbars.ToList();

        Toast.ShowPersistent(title, "body");

        return _snackbar.ShownSnackbars.Except(before).Should().ContainSingle().Subject;
    }

    /// <summary>
    /// MudBlazor's real service, re-implementing only <see cref="ISnackbar.Remove"/> so a test can
    /// tell a removal the toast service made (through the interface) from one a close made
    /// (MudBlazor calls its own public <c>Remove</c> directly, bypassing this override).
    /// </summary>
    private sealed class RecordingSnackbarService()
        : SnackbarService(
            new TestNavigationManager(),
            TimeProvider.System,
            Options.Create(new SnackbarConfiguration { MaxDisplayedSnackbars = Cap })),
        ISnackbar
    {
        public List<Snackbar> RemovedByToastService { get; } = [];

        void ISnackbar.Remove(Snackbar snackbar)
        {
            RemovedByToastService.Add(snackbar);
            Remove(snackbar);
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }
}
