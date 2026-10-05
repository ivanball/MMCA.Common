using MMCA.Common.UI.Theme;

namespace MMCA.Common.UI.Maui.Theme;

/// <summary>
/// The MAUI <see cref="IInitialThemeModeSource"/>: the Day/Dark mode <see cref="MauiThemeStore"/>
/// mirrored into device preferences on the previous run, read synchronously so
/// <c>MmcaThemeProviders</c> renders its FIRST frame in that mode instead of light-then-dark.
/// <see langword="null"/> on a first launch, which leaves the decision to the JS path as before.
/// Read on every access rather than cached, so a value saved later in the session is never stale.
/// </summary>
internal sealed class MauiInitialThemeModeSource : IInitialThemeModeSource
{
    /// <inheritdoc />
    public bool? IsDarkMode => MauiThemeStore.ReadStoredIsDarkMode();
}
