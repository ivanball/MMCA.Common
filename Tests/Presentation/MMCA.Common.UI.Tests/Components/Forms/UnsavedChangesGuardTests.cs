using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Forms;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Forms;

public sealed class UnsavedChangesGuardTests : BunitTestBase
{
    [Fact]
    public void Accessor_TakesPrecedence_OverIsDirtyParameter()
    {
        // IsDirty=false but the live accessor reports dirty — the guard must honor the accessor,
        // which is the whole point of the param-lag fix.
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, false)
            .Add(c => c.IsDirtyAccessor, () => true));

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeTrue();
    }

    [Fact]
    public void FallsBackToIsDirty_WhenNoAccessor()
    {
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, true));

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeTrue();
    }

    [Fact]
    public void Accessor_OverridesStaleTrueParameter_AfterSaveClearsDirty()
    {
        // The exact param-lag bug: the form just saved (live state clean) and navigates before the
        // IsDirty parameter re-renders. The accessor must win so NO false prompt is shown.
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, true)
            .Add(c => c.IsDirtyAccessor, () => false));

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeFalse();
    }

    [Fact]
    public void WhenClean_DoesNotConfirmNavigation()
    {
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, false));

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeFalse();
    }

    [Fact]
    public async Task InternalNavigation_WhenClean_ProceedsWithoutAsking()
    {
        var dialog = UseDialog(answer: false);
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p.Add(c => c.IsDirty, false));

        await NavigateAsync(cut, "/elsewhere");

        CurrentPath().Should().Be("/elsewhere");
        dialog.Verify(
            d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
    }

    [Fact]
    public async Task InternalNavigation_WhenDirtyAndTheUserStays_IsPrevented_WithTheLocalizedDefaults()
    {
        var dialog = UseDialog(answer: false);
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p.Add(c => c.IsDirty, true));

        await NavigateAsync(cut, "/elsewhere");

        CurrentPath().Should().NotBe("/elsewhere", "declining the prompt keeps the user on the dirty form");
        dialog.Verify(
            d => d.ConfirmAsync(
                "Unsaved changes",
                "You have unsaved changes. Leave this page anyway?",
                "Leave",
                "Stay"),
            Times.Once());
    }

    [Fact]
    public async Task InternalNavigation_WhenDirtyAndTheUserLeaves_Proceeds_WithTheSuppliedTexts()
    {
        var dialog = UseDialog(answer: true);
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, true)
            .Add(c => c.Title, "Discard order?")
            .Add(c => c.Message, "The cart edits are lost.")
            .Add(c => c.LeaveButtonText, "Discard")
            .Add(c => c.StayButtonText, "Keep editing"));

        await NavigateAsync(cut, "/elsewhere");

        CurrentPath().Should().Be("/elsewhere");
        dialog.Verify(
            d => d.ConfirmAsync("Discard order?", "The cart edits are lost.", "Discard", "Keep editing"),
            Times.Once());
    }

    [Fact]
    public async Task InternalNavigation_ConsultsTheLiveAccessor_NotTheStaleParameter()
    {
        var dialog = UseDialog(answer: false);
        var cut = RenderUnderTest<UnsavedChangesGuard>(p => p
            .Add(c => c.IsDirty, true)
            .Add(c => c.IsDirtyAccessor, () => false));

        await NavigateAsync(cut, "/elsewhere");

        CurrentPath().Should().Be("/elsewhere", "the form saved, so the live state is clean");
        dialog.Verify(
            d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
    }

    private Mock<IAppDialogService> UseDialog(bool answer)
    {
        var dialog = new Mock<IAppDialogService>();
        dialog.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(answer);
        Services.AddSingleton(dialog.Object);
        return dialog;
    }

    private static Task NavigateAsync(IRenderedComponent<UnsavedChangesGuard> cut, string uri)
    {
        var navigation = cut.Services.GetRequiredService<NavigationManager>();
        return cut.InvokeAsync(() => navigation.NavigateTo(uri));
    }

    private string CurrentPath() => new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath;
}
