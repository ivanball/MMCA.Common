namespace MMCA.Common.UI.Theme;

/// <summary>
/// An optional, synchronous source for the Day/Dark mode (ADR-028) the FIRST render should use.
/// <para>
/// <see cref="ThemeService"/> resolves the stored or OS preference through JS interop, which only
/// runs after the first interactive render, so without a source <c>MmcaThemeProviders</c> paints the
/// light palette first and flips a moment later. A head that already knows the stored preference
/// without JS (a MAUI Blazor Hybrid head reading device preferences) registers an implementation,
/// and <c>MmcaThemeProviders</c> reads it during initialization. The JS path still runs afterwards
/// and stays authoritative. Web heads register nothing and keep today's behavior.
/// </para>
/// </summary>
public interface IInitialThemeModeSource
{
    /// <summary>
    /// Gets the mode to render first: <see langword="true"/> for dark, <see langword="false"/> for
    /// light, or <see langword="null"/> when the source does not know (no stored preference yet),
    /// which leaves the first render unchanged.
    /// </summary>
    bool? IsDarkMode { get; }
}
