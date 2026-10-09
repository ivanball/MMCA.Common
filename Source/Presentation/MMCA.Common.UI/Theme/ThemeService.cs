using Microsoft.JSInterop;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Theme;

/// <summary>
/// Owns the Day/Dark theme preference (ADR-028). Holds the current mode, persists it to a non-HttpOnly
/// cookie + localStorage (via <c>theme.js</c>), and raises <see cref="OnChange"/> so the root layout's
/// <c>MudThemeProvider</c> and the app-bar toggle stay in sync. The first-visit default is the OS
/// <c>prefers-color-scheme</c>, used only when no stored value exists.
/// <para>
/// JS interop is only available after the first interactive render, so <see cref="InitializeAsync"/> must
/// be called from <c>OnAfterRenderAsync(firstRender: true)</c>, never during SSR prerender.
/// </para>
/// </summary>
/// <param name="jsRuntime">The host's JS runtime used to read/write the preference.</param>
public sealed class ThemeService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MMCA.Common.UI/theme.js";
    private readonly LazyJsModule _module = new(jsRuntime, ModulePath);

    /// <summary>Whether dark mode is currently active.</summary>
    public bool IsDarkMode { get; private set; }

    /// <summary>Whether <see cref="InitializeAsync"/> has resolved the stored/system preference yet.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Raised whenever <see cref="IsDarkMode"/> changes so subscribers can re-render.</summary>
    public event EventHandler? OnChange;

    /// <summary>
    /// Resolves the initial mode from the stored cookie/localStorage value, falling back to the OS
    /// preference when nothing is stored. Safe to call repeatedly; only the first call does work.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        var module = await GetModuleAsync();
        var stored = await module.InvokeAsync<string?>("get");
        IsDarkMode = stored is not null
            ? string.Equals(stored, "dark", StringComparison.OrdinalIgnoreCase)
            : await module.InvokeAsync<bool>("systemPrefersDark");

        IsInitialized = true;
        OnChange?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves the initial mode, preferring a native store the head read before the WebView painted
    /// (an <see cref="IInitialThemeModeSource"/>). A known native value is authoritative: it is adopted
    /// as is, and the WebView cookie/localStorage is reseeded with it, so a stale value there can
    /// neither win now nor be mirrored back over the native one. A <see langword="null"/> value
    /// resolves exactly as <see cref="InitializeAsync"/> does. Only the first initialization does work.
    /// </summary>
    /// <param name="nativeIsDarkMode">The native store's mode, or <see langword="null"/> when unknown.</param>
    /// <returns>A task that completes when the mode is resolved.</returns>
    internal Task InitializeAtStartupAsync(bool? nativeIsDarkMode) =>
        nativeIsDarkMode is bool isDark ? InitializeFromNativeAsync(isDark) : InitializeAsync();

    private async Task InitializeFromNativeAsync(bool isDarkMode)
    {
        if (IsInitialized)
        {
            return;
        }

        IsDarkMode = isDarkMode;
        IsInitialized = true;
        OnChange?.Invoke(this, EventArgs.Empty);

        // Reseeding is the last step: a failure leaves the native value in force, and the caller's
        // best-effort guard reports it.
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("set", isDarkMode ? "dark" : "light");
    }

    /// <summary>Sets the mode, persists it, and notifies subscribers.</summary>
    /// <param name="isDarkMode"><see langword="true"/> for dark, <see langword="false"/> for light.</param>
    public async Task SetDarkModeAsync(bool isDarkMode)
    {
        // Persist first: a failed import or call leaves the flag (and every subscriber) on the
        // mode that is actually stored, instead of flipped with nothing persisted and no OnChange.
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("set", isDarkMode ? "dark" : "light");
        IsDarkMode = isDarkMode;
        OnChange?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Flips between light and dark, persisting the new value.</summary>
    public Task ToggleAsync() => SetDarkModeAsync(!IsDarkMode);

    private Task<IJSObjectReference> GetModuleAsync() => _module.GetOrImportAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _module.DisposeAsync();
}
