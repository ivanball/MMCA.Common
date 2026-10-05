using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components.Forms;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// X-08 (Store run 1): the delete confirmation ignored the Escape key. A modal that asks before
/// something irreversible must let the keyboard user back out the standard way: Escape closes it,
/// and closing it that way counts as Cancel (<see cref="DeleteConfirmation.ShowAsync"/> resolves
/// to <see langword="null"/>, exactly like the Cancel button), never as Delete.
/// <para>
/// MudBlazor delivers keystrokes to an open dialog through its key interceptor (a JS listener that
/// calls back into .NET), which bUnit has no browser to drive, so the test performs that callback
/// itself with an Escape key-down for the dialog's container: the same call the browser would make.
/// The initial-focus half of X-08 needs a real browser and is pinned in
/// <c>MMCA.Common.UI.E2E.Tests</c> (<c>DeleteConfirmationKeyboardE2ETests</c>).
/// </para>
/// </summary>
public sealed class DeleteConfirmationEscapeTests : BunitTestBase
{
    private const string KeyInterceptorConnect = "mudKeyInterceptor.connect";

    [Fact]
    public async Task Escape_ClosesTheDialog_AsCancel()
    {
        var providers = RenderMudProviders();
        var cut = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Customer"));

        Task<bool?>? show = null;
        await cut.InvokeAsync(() => { show = cut.Instance.ShowAsync("Jane Doe"); });
        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Jane Doe").Should().BeTrue());

        await PressEscapeInTheDialogAsync(providers.Dialog);

        var finished = await Task.WhenAny(show!, Task.Delay(TimeSpan.FromSeconds(3), Xunit.TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(show, "Escape must close the delete confirmation instead of being ignored");
        (await show!).Should().BeNull("dismissing with Escape is a Cancel, never a confirmation");
        providers.Dialog.FindAll("[data-testid=confirm-delete]").Should().BeEmpty("the dialog is gone");
    }

    private async Task PressEscapeInTheDialogAsync(IRenderedComponent<MudBlazor.MudDialogProvider> dialog)
    {
        // The dialog container subscribes its own element id with the key interceptor when it opens.
        var containerId = dialog.Find(".mud-dialog-container").Id;
        var connect = JSInterop.Invocations
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
        await dialog.InvokeAsync(async () =>
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
