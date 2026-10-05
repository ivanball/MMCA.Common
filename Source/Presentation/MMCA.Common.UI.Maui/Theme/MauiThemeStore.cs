namespace MMCA.Common.UI.Maui.Theme;

/// <summary>
/// The native copy of the Day/Dark preference (ADR-028) on a MAUI Blazor Hybrid head, so the native
/// chrome starts in the right theme before the WebView paints.
/// <para>
/// The preference itself lives in the WebView's storage (<c>theme.js</c>), which nothing native can
/// read until Blazor has started, so a cold dark launch drew the page behind the WebView light first.
/// <c>NativeThemeSync</c> mirrors every resolved or toggled value here, in device preferences, and
/// <see cref="MainPageBase"/> applies the stored value from its constructor, on the startup path ahead
/// of the first WebView frame. Reads and writes <c>Preferences.Default</c> directly for the same reason
/// <c>MauiCultureStore</c> does: the startup read is synchronous.
/// </para>
/// </summary>
internal static class MauiThemeStore
{
    /// <summary>
    /// The device-preference key. Changing it silently drops every installed app back to the OS theme
    /// for its next cold launch.
    /// </summary>
    private const string PreferenceKey = "mmca.theme";

    private const string Dark = "dark";
    private const string Light = "light";

    /// <summary>Persists the resolved mode. Best-effort, mirroring the rest of the layer.</summary>
    /// <param name="isDarkMode"><see langword="true"/> for dark, <see langword="false"/> for light.</param>
    public static void Save(bool isDarkMode) => Preferences.Default.Set(PreferenceKey, isDarkMode ? Dark : Light);

    /// <summary>
    /// Applies the stored mode to <see cref="Application.UserAppTheme"/>. Does nothing when no mode
    /// has been stored yet (first launch keeps following the OS, exactly as the web preference does)
    /// or when no application exists.
    /// </summary>
    public static void ApplyStoredTheme()
    {
        var app = Application.Current;
        var theme = Preferences.Default.Get<string?>(PreferenceKey, null) switch
        {
            Dark => AppTheme.Dark,
            Light => AppTheme.Light,
            _ => AppTheme.Unspecified
        };

        if (app is null || theme == AppTheme.Unspecified || app.UserAppTheme == theme)
        {
            return;
        }

        app.UserAppTheme = theme;
    }
}
