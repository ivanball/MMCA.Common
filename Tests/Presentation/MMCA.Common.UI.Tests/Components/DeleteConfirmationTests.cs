using AwesomeAssertions;
using Bunit;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

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
}
