using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.UI.Components.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components.Notifications;

/// <summary>
/// <see cref="StaleToastSweeper"/> (opt-in, <c>Layout:ClearToastsOnNavigation</c>): a navigation removes
/// the toasts that had been on screen for longer than <see cref="StaleToastSweeper.Grace"/>, and keeps
/// one raised in the same moment as the navigation (an action that reports its outcome and moves on).
/// </summary>
public sealed class StaleToastSweeperTests : BunitTestBase
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));

    public StaleToastSweeperTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public void Navigation_RemovesAToastThatWasAlreadyShowing()
    {
        var snackbar = Services.GetRequiredService<ISnackbar>();
        RenderUnderTest<StaleToastSweeper>(_ => { });

        snackbar.Add("Item added to cart!", Severity.Success);
        _time.Advance(TimeSpan.FromSeconds(2));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/checkout");

        snackbar.ShownSnackbars.Should().BeEmpty("a toast about the page being left does not follow the user");
    }

    [Fact]
    public void Navigation_KeepsAToastRaisedAsPartOfTheNavigation()
    {
        var snackbar = Services.GetRequiredService<ISnackbar>();
        RenderUnderTest<StaleToastSweeper>(_ => { });

        snackbar.Add("Order created. Taking you to it.", Severity.Info);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/orders/1");

        snackbar.ShownSnackbars.Should().ContainSingle("a toast raised in the same moment is about the page being opened");
    }

    [Fact]
    public void QueryStringRewriteOnTheSamePage_KeepsAToastThatWasAlreadyShowing()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/products");
        var snackbar = Services.GetRequiredService<ISnackbar>();
        RenderUnderTest<StaleToastSweeper>(_ => { });

        snackbar.Add("Deleted", Severity.Success);
        _time.Advance(TimeSpan.FromSeconds(2));
        navigation.NavigateTo("/products?sort=name", new NavigationOptions { ReplaceHistoryEntry = true });

        snackbar.ShownSnackbars.Should().ContainSingle("a grid sort mirrored into the URL does not leave the page");
    }

    [Fact]
    public async Task AfterDisposal_NavigationLeavesToastsAlone()
    {
        var snackbar = Services.GetRequiredService<ISnackbar>();
        RenderUnderTest<StaleToastSweeper>(_ => { });
        snackbar.Add("Saved", Severity.Success);
        _time.Advance(TimeSpan.FromSeconds(2));

        await DisposeComponentsAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/elsewhere");

        snackbar.ShownSnackbars.Should().ContainSingle("a disposed sweeper is unsubscribed from navigation");
    }
}
