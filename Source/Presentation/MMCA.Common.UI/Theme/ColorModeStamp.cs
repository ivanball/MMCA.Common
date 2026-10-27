using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Theme;

/// <summary>
/// The opt-in colour-mode hook of <see cref="MmcaThemeProviders"/> (<c>Layout:StampColorModeOnDocument</c>):
/// mirrors the Day/Dark mode onto <c>&lt;html data-mmca-color-mode="light|dark"&gt;</c> so an app's own
/// stylesheet can follow it. The provider calls it after its first interactive render and after every
/// theme change; with the setting off (the default) it does nothing.
/// </summary>
internal static class ColorModeStamp
{
    /// <summary>Stamps <paramref name="isDarkMode"/> on the document when the host opted in and the render is interactive.</summary>
    /// <param name="js">The JS runtime.</param>
    /// <param name="services">The scope's services; a head with no layout settings registered never stamps.</param>
    /// <param name="isInteractive">Whether the current render is interactive (JS interop is unavailable otherwise).</param>
    /// <param name="isDarkMode">The mode to stamp.</param>
    /// <returns>A task that completes when the stamp was attempted.</returns>
    public static async Task ApplyAsync(IJSRuntime js, IServiceProvider services, bool isInteractive, bool isDarkMode)
    {
        if (!isInteractive || services.GetService<IOptions<LayoutSettings>>()?.Value.StampColorModeOnDocument != true)
        {
            return;
        }

        // Best-effort, like the interactivity marker: an app's styling hook must never break theme init.
        await ThemeInterop.TryAsync(async () =>
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/MMCA.Common.UI/theme.js");
            await module.InvokeVoidAsync("stampColorMode", isDarkMode ? "dark" : "light");
        }).ConfigureAwait(false);
    }
}
