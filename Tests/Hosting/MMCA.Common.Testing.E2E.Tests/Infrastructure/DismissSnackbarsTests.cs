using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Moq;

namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>
/// <c>DismissSnackbarsAsync</c>, driven through mocked locators instead of a browser: it returns at
/// once with nothing on screen, closes each toast through its close icon, falls back to the
/// content-action button only for a toast with no close icon (a <c>RequireInteraction</c>
/// notification), narrows to the toasts carrying the given text, treats a click that loses the race
/// with the toast's own timer as done, and closes a queued toast on the next round. The MudBlazor
/// markup itself is exercised by the consumers' E2E suites.
/// </summary>
public sealed class DismissSnackbarsTests
{
    private readonly Mock<IPage> _page = new();
    private readonly Mock<ILocator> _snackbars = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DismissSnackbarsTests"/> class: the page's
    /// snackbar locator is the mock each test scripts.
    /// </summary>
    public DismissSnackbarsTests() =>
        _page.Setup(p => p.Locator(".mud-snackbar", It.IsAny<PageLocatorOptions?>())).Returns(_snackbars.Object);

    /// <summary>With no toast on screen the helper returns without reaching for one.</summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task NothingOnScreen_ReturnsWithoutTouchingAToast()
    {
        _snackbars.Setup(s => s.CountAsync()).ReturnsAsync(0);

        await _page.Object.DismissSnackbarsAsync();

        _snackbars.Verify(s => s.Nth(It.IsAny<int>()), Times.Never);
    }

    /// <summary>A toast with a close icon is closed through it, never through its action.</summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task EachToast_IsClosedThroughItsCloseIcon_AndTheActionIsNeverClicked()
    {
        _snackbars.SetupSequence(s => s.CountAsync()).ReturnsAsync(2).ReturnsAsync(0);
        var first = Toast(hasCloseIcon: true, hasAction: true);
        var second = Toast(hasCloseIcon: true, hasAction: false);
        _snackbars.Setup(s => s.Nth(0)).Returns(first.Toast.Object);
        _snackbars.Setup(s => s.Nth(1)).Returns(second.Toast.Object);

        await _page.Object.DismissSnackbarsAsync();

        first.CloseIcon.Verify(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()), Times.Once);
        second.CloseIcon.Verify(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()), Times.Once);
        first.Action.Verify(
            a => a.ClickAsync(It.IsAny<LocatorClickOptions?>()),
            Times.Never,
            "an action can have a side effect, so a toast with a close icon is closed through the icon");
    }

    /// <summary>A RequireInteraction toast has no close icon, so its content action closes it.</summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task AToastWithNoCloseIcon_IsClosedThroughItsContentAction()
    {
        _snackbars.SetupSequence(s => s.CountAsync()).ReturnsAsync(1).ReturnsAsync(0);
        var toast = Toast(hasCloseIcon: false, hasAction: true);
        _snackbars.Setup(s => s.Nth(0)).Returns(toast.Toast.Object);

        await _page.Object.DismissSnackbarsAsync();

        toast.Action.Verify(a => a.ClickAsync(It.IsAny<LocatorClickOptions?>()), Times.Once);
    }

    /// <summary>With a text filter, only the toasts carrying that text are counted and closed.</summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task WithText_OnlyTheMatchingToastsAreCounted()
    {
        var matching = new Mock<ILocator>();
        _snackbars
            .Setup(s => s.Filter(It.Is<LocatorFilterOptions?>(o => o != null && o.HasText == "Saved.")))
            .Returns(matching.Object);
        matching.SetupSequence(m => m.CountAsync()).ReturnsAsync(1).ReturnsAsync(0);
        var toast = Toast(hasCloseIcon: true, hasAction: false);
        matching.Setup(m => m.Nth(0)).Returns(toast.Toast.Object);

        await _page.Object.DismissSnackbarsAsync("Saved.");

        toast.CloseIcon.Verify(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()), Times.Once);
        _snackbars.Verify(s => s.CountAsync(), Times.Never, "the unfiltered set includes toasts the caller did not ask about");
    }

    /// <summary>A toast that expires under the click detaches, which is the outcome wanted.</summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task AClickThatLosesTheRaceWithTheToastsTimer_IsTreatedAsDone()
    {
        _snackbars.SetupSequence(s => s.CountAsync()).ReturnsAsync(1).ReturnsAsync(0);
        var toast = Toast(hasCloseIcon: true, hasAction: false);
        toast.CloseIcon
            .Setup(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()))
            .ThrowsAsync(new TimeoutException("the element detached"));
        _snackbars.Setup(s => s.Nth(0)).Returns(toast.Toast.Object);

        var act = () => _page.Object.DismissSnackbarsAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Round one closes the visible toast; while it hides, a queued one takes its slot and the count
    /// never reaches zero within the round's wait, so round two closes the queued one.
    /// </summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    [Fact]
    public async Task AQueuedToastThatTakesAFreedSlot_IsClosedOnTheNextRound()
    {
        var queuedClosed = false;
        _snackbars.Setup(s => s.CountAsync()).ReturnsAsync(() => queuedClosed ? 0 : 1);
        var first = Toast(hasCloseIcon: true, hasAction: false);
        var queued = Toast(hasCloseIcon: true, hasAction: false);
        queued.CloseIcon
            .Setup(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()))
            .Callback(() => queuedClosed = true)
            .Returns(Task.CompletedTask);
        _snackbars.SetupSequence(s => s.Nth(0)).Returns(first.Toast.Object).Returns(queued.Toast.Object);

        await _page.Object.DismissSnackbarsAsync();

        first.CloseIcon.Verify(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()), Times.Once);
        queued.CloseIcon.Verify(c => c.ClickAsync(It.IsAny<LocatorClickOptions?>()), Times.Once);
    }

    private static (Mock<ILocator> Toast, Mock<ILocator> CloseIcon, Mock<ILocator> Action) Toast(bool hasCloseIcon, bool hasAction)
    {
        var toast = new Mock<ILocator>();
        var closeIcon = Button(hasCloseIcon);
        var action = Button(hasAction);
        toast.Setup(t => t.Locator(".mud-snackbar-close-button", It.IsAny<LocatorLocatorOptions?>())).Returns(closeIcon.Object);
        toast.Setup(t => t.Locator(".mud-snackbar-content-action button", It.IsAny<LocatorLocatorOptions?>())).Returns(action.Object);
        return (toast, closeIcon, action);
    }

    private static Mock<ILocator> Button(bool present)
    {
        var button = new Mock<ILocator>();
        button.Setup(b => b.CountAsync()).ReturnsAsync(present ? 1 : 0);
        button.Setup(b => b.First).Returns(button.Object);
        button.Setup(b => b.ClickAsync(It.IsAny<LocatorClickOptions?>())).Returns(Task.CompletedTask);
        return button;
    }
}
