using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Forms;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// bUnit tests for <see cref="DeleteConfirmation"/> — drives the imperative <c>ShowAsync</c> dialog
/// flow through the MudBlazor dialog provider and asserts the confirm/cancel result and prompt text.
/// </summary>
/// <remarks>
/// <c>ShowAsync</c> is launched inside a block-body <c>InvokeAsync</c> lambda (returning void) so it
/// binds to the <c>Action</c> overload and is NOT awaited — the returned task only completes once a
/// dialog button is clicked later in the test.
/// </remarks>
public sealed class DeleteConfirmationTests : BunitTestBase
{
    private const string KeyInterceptorConnect = "mudKeyInterceptor.connect";

    [Fact]
    public async Task ConfirmingDelete_ReturnsTrue()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Event"));

        Task<bool?>? show = null;
        await cut.InvokeAsync(() => { show = cut.Instance.ShowAsync("Annual Meetup"); });

        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Annual Meetup").Should().BeTrue());
        providers.Dialog.ClickButtonByText("Delete");

        (await show!).Should().BeTrue();
    }

    [Fact]
    public async Task CancellingDelete_ReturnsNull()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Speaker"));

        Task<bool?>? show = null;
        await cut.InvokeAsync(() => { show = cut.Instance.ShowAsync("Jane Doe"); });

        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Jane Doe").Should().BeTrue());
        providers.Dialog.ClickButtonByText("Cancel");

        (await show!).Should().BeNull();
    }

    [Fact]
    public async Task ShowAsync_RendersEntityTypeAndNameInPrompt()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Event"));

        await cut.InvokeAsync(() => { _ = cut.Instance.ShowAsync("Annual Meetup"); });

        await providers.Dialog.WaitForAssertionAsync(() =>
        {
            providers.Dialog.Markup.Should().Contain("Delete Event");
            providers.Dialog.Markup.Should().Contain("delete event");
            providers.Dialog.Markup.Should().Contain("Annual Meetup");
        });
    }

    // ── Escape key (X-08, Store run 1) ──
    // The delete confirmation ignored the Escape key. A modal that asks before something
    // irreversible must let the keyboard user back out the standard way: Escape closes it, and
    // closing it that way counts as Cancel (ShowAsync resolves to null, exactly like the Cancel
    // button), never as Delete.
    // MudBlazor delivers keystrokes to an open dialog through its key interceptor (a JS listener that
    // calls back into .NET), which bUnit has no browser to drive, so the test performs that callback
    // itself with an Escape key-down for the dialog's container: the same call the browser would make.
    // The initial-focus half of X-08 needs a real browser and is pinned in MMCA.Common.UI.E2E.Tests
    // (DeleteConfirmationKeyboardE2ETests).
    [Fact]
    public async Task Escape_ClosesTheDialog_AsCancel()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Customer"));

        Task<bool?>? show = null;
        await cut.InvokeAsync(() => { show = cut.Instance.ShowAsync("Jane Doe"); });
        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Jane Doe").Should().BeTrue());

        await PressEscapeInTheDialogAsync(JSInterop, providers.Dialog);

        var finished = await Task.WhenAny(show!, Task.Delay(TimeSpan.FromSeconds(3), Xunit.TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(show, "Escape must close the delete confirmation instead of being ignored");
        (await show!).Should().BeNull("dismissing with Escape is a Cancel, never a confirmation");
        providers.Dialog.FindAll("[data-testid=confirm-delete]").Should().BeEmpty("the dialog is gone");
    }

    /// <summary>
    /// Delivers an Escape key-down to the open dialog the way the browser would, through the callback
    /// MudBlazor's key interceptor registered for the dialog container. Shared with
    /// <see cref="ConfirmationDialogTests"/>.
    /// </summary>
    internal static Task PressEscapeInTheDialogAsync(BunitJSInterop jsInterop, IRenderedComponent<MudBlazor.MudDialogProvider> dialog)
    {
        // The dialog container subscribes its own element id with the key interceptor when it opens.
        var containerId = dialog.Find(".mud-dialog-container").Id;
        var connect = jsInterop.Invocations
            .Where(i => i.Identifier == KeyInterceptorConnect)
            .LastOrDefault(i => i.Arguments.Count > 1 && Equals(i.Arguments[1], containerId));
        connect.Identifier.Should().Be(
            KeyInterceptorConnect,
            "the open dialog registers its container with MudBlazor's key interceptor (precondition)");

        // arguments[0] is the DotNetObjectReference<KeyInterceptorService> the browser calls back on.
        var reference = connect.Arguments[0]!;
        var service = reference.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!.GetValue(reference)!;
        var onKeyDown = service.GetType().GetMethod("OnKeyDown", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        onKeyDown.Should().NotBeNull("the key interceptor exposes the key-down callback the browser invokes (precondition)");

        var escape = new KeyboardEventArgs { Key = "Escape", Code = "Escape", Type = "keydown" };
        return dialog.InvokeAsync(async () =>
        {
            var outcome = onKeyDown!.Invoke(service, [containerId, escape]);
            if (outcome is Task pending)
            {
                await pending;
            }
            else if (outcome is ValueTask pendingValue)
            {
                await pendingValue;
            }
        });
    }
}
