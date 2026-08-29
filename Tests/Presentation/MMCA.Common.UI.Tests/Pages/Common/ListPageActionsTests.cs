using AwesomeAssertions;
using Bunit;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components;
using MMCA.Common.UI.Pages.Common;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// bUnit tests for <see cref="ListPageActions"/>: the reload dispatch that has to hit whichever
/// layout is actually rendered, and the confirm-delete-reload flow every list page repeats. The
/// failure branches are the load-bearing ones: a rejected confirmation must not delete, a failed
/// <see cref="Result"/> must not toast success or reload, and a cancellation (component disposal or
/// an InteractiveAuto render-mode transition) must stay silent.
/// </summary>
public sealed class ListPageActionsTests : BunitTestBase
{
    // Deliberately NOT registered in DI: DeleteWithConfirmationAsync takes the toast service as
    // an argument, so the flow's own toasts are asserted here while the components rendered by
    // RenderMudProviders keep the base class's real Mud-backed facade.
    private readonly Mock<IToastService> _toast = new();

    [Fact]
    public async Task ReloadActiveLayout_OnMobile_ResetsTheMobileList()
    {
        var fetches = 0;
        var list = RenderMobileList(() => fetches++);
        var before = fetches;

        await list.InvokeAsync(() =>
            ListPageActions.ReloadActiveLayoutAsync<string>(true, list.Instance, null));

        fetches.Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task ReloadActiveLayout_OnDesktop_LeavesTheMobileListAlone()
    {
        var fetches = 0;
        var list = RenderMobileList(() => fetches++);
        var before = fetches;

        await list.InvokeAsync(() =>
            ListPageActions.ReloadActiveLayoutAsync<string>(false, list.Instance, null));

        fetches.Should().Be(before);
    }

    [Fact]
    public async Task ReloadActiveLayout_WithNeitherLayoutRendered_IsANoOp()
    {
        var act = async () => await ListPageActions.ReloadActiveLayoutAsync<string>(true, null, null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Delete_WhenConfirmed_TogglesSuccessAndReloads()
    {
        var providers = RenderMudProviders();
        var confirm = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Session"));

        var deleted = 0;
        var reloaded = 0;
        Task? flow = null;
        await confirm.InvokeAsync(() =>
        {
            flow = ListPageActions.DeleteWithConfirmationAsync(
                confirm.Instance,
                "Intro to Blazor",
                () =>
                {
                    deleted++;
                    return Task.FromResult(Result.Success());
                },
                _toast.Object,
                "Session deleted",
                _ => "unused",
                () =>
                {
                    reloaded++;
                    return Task.CompletedTask;
                });
        });

        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Intro to Blazor").Should().BeTrue());
        providers.Dialog.ClickButtonByText("Delete");
        await flow!;

        deleted.Should().Be(1);
        reloaded.Should().Be(1);
        _toast.Verify(t => t.Success("Session deleted"), Times.Once);
    }

    [Fact]
    public async Task Delete_WhenCancelled_NeverCallsTheDelete()
    {
        var providers = RenderMudProviders();
        var confirm = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Session"));

        var deleted = 0;
        var reloaded = 0;
        Task? flow = null;
        await confirm.InvokeAsync(() =>
        {
            flow = ListPageActions.DeleteWithConfirmationAsync(
                confirm.Instance,
                "Intro to Blazor",
                () =>
                {
                    deleted++;
                    return Task.FromResult(Result.Success());
                },
                _toast.Object,
                "Session deleted",
                _ => "unused",
                () =>
                {
                    reloaded++;
                    return Task.CompletedTask;
                });
        });

        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Intro to Blazor").Should().BeTrue());
        providers.Dialog.ClickButtonByText("Cancel");
        await flow!;

        deleted.Should().Be(0);
        reloaded.Should().Be(0);
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Delete_WhenTheDeleteFails_ToastsTheMappedErrorAndSkipsTheReload()
    {
        var providers = RenderMudProviders();
        var confirm = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Session"));

        var reloaded = 0;
        Task? flow = null;
        await confirm.InvokeAsync(() =>
        {
            flow = ListPageActions.DeleteWithConfirmationAsync(
                confirm.Instance,
                "Intro to Blazor",
                () => Task.FromResult(
                    Result.Failure(Error.Conflict("Session.InUse", "Session is on the schedule"))),
                _toast.Object,
                "Session deleted",
                result => result.Errors[0].Message,
                () =>
                {
                    reloaded++;
                    return Task.CompletedTask;
                });
        });

        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Intro to Blazor").Should().BeTrue());
        providers.Dialog.ClickButtonByText("Delete");
        await flow!;

        reloaded.Should().Be(0);
        _toast.Verify(t => t.Error("Session is on the schedule"), Times.Once);
    }

    [Fact]
    public async Task Delete_WhenTheDeleteIsCancelled_StaysSilent()
    {
        // Component disposal or an InteractiveAuto render-mode transition cancels the in-flight
        // call; surfacing that as an error toast would blame the user for navigating away.
        var providers = RenderMudProviders();
        var confirm = RenderUnderTest<DeleteConfirmation>(p => p.Add(c => c.EntityType, "Session"));

        Task? flow = null;
        await confirm.InvokeAsync(() =>
        {
            flow = ListPageActions.DeleteWithConfirmationAsync(
                confirm.Instance,
                "Intro to Blazor",
                () => throw new OperationCanceledException(),
                _toast.Object,
                "Session deleted",
                _ => "unused",
                () => Task.CompletedTask);
        });

        await providers.Dialog.WaitForAssertionAsync(() => providers.Dialog.HasText("Intro to Blazor").Should().BeTrue());
        providers.Dialog.ClickButtonByText("Delete");

        var act = async () => await flow!;
        await act.Should().NotThrowAsync();
        _toast.VerifyNoOtherCalls();
    }

    private IRenderedComponent<MobileInfiniteScrollList<string>> RenderMobileList(Action onFetch) =>
        RenderUnderTest<MobileInfiniteScrollList<string>>(p => p
            .Add(c => c.CardTemplate, item => item)
            .Add(c => c.FetchPageResult, (_, _, _) =>
            {
                onFetch();
                return Task.FromResult(
                    Result.Success<(IReadOnlyList<string> Items, int TotalItems)>((["Alpha"], 1)));
            }));
}
