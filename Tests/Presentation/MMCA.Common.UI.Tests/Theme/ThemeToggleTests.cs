using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Theme;

namespace MMCA.Common.UI.Tests.Theme;

/// <summary>
/// Covers <see cref="ThemeToggle"/>, the app-bar Day/Dark switch (ADR-028): clicking flips the scoped
/// <see cref="ThemeService"/>, an external theme change re-renders the button, disposal unsubscribes
/// so the long-lived service never calls back into a dead component, and a change that was already
/// being raised when disposal landed is ignored instead of throwing back at the publisher.
/// </summary>
public sealed class ThemeToggleTests : BunitTestBase
{
    private static readonly FieldInfo OnChangeField = typeof(ThemeService)
        .GetField("OnChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void ClickingTheToggle_FlipsTheThemeService()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        var cut = RenderUnderTest<ThemeToggle>(_ => { });

        cut.Find("button").Click();

        cut.WaitForAssertion(() => themeService.IsDarkMode.Should().BeTrue(
            "the toggle owns no state of its own; it drives the scoped ThemeService"));
    }

    [Fact]
    public async Task ThemeServiceChange_RerendersTheToggle()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        var cut = RenderUnderTest<ThemeToggle>(_ => { });
        string lightMarkup = cut.Markup;

        await cut.InvokeAsync(() => themeService.SetDarkModeAsync(true));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().NotBe(
            lightMarkup, "both the icon and the aria-label read from ThemeService.IsDarkMode"));
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromTheThemeServiceOnChangeEvent()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        RenderUnderTest<ThemeToggle>(_ => { });
        OnChangeField.GetValue(themeService).Should().NotBeNull("the component subscribes in OnInitialized");

        await DisposeComponentsAsync();

        OnChangeField.GetValue(themeService).Should().BeNull(
            "the scoped ThemeService outlives the component, so Dispose must unsubscribe");
    }

    [Fact]
    public async Task ThemeChangeRaisedAfterDisposal_DoesNotThrow()
    {
        var themeService = Services.GetRequiredService<ThemeService>();
        RenderUnderTest<ThemeToggle>(_ => { });

        // OnChange is raised synchronously and its invocation list is captured at the raise, so a
        // subscriber disposed partway through the chain (one scoped ThemeService feeds both ThemeToggle
        // placements and MmcaThemeProviders) still gets called. Capture the delegate to replay that race.
        var subscribers = (EventHandler?)OnChangeField.GetValue(themeService);
        subscribers.Should().NotBeNull();

        await DisposeComponentsAsync();

        var raise = () => subscribers!.Invoke(themeService, EventArgs.Empty);

        raise.Should().NotThrow(
            "a rerender dispatched onto a disposed component must not surface to the publisher "
            + "and cut off the subscribers queued behind it");
    }
}
