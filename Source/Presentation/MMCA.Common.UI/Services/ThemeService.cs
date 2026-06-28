using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services;

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
    private IJSObjectReference? _module;

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

    /// <summary>Sets the mode, persists it, and notifies subscribers.</summary>
    /// <param name="isDarkMode"><see langword="true"/> for dark, <see langword="false"/> for light.</param>
    public async Task SetDarkModeAsync(bool isDarkMode)
    {
        IsDarkMode = isDarkMode;
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("set", isDarkMode ? "dark" : "light");
        OnChange?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Flips between light and dark, persisting the new value.</summary>
    public Task ToggleAsync() => SetDarkModeAsync(!IsDarkMode);

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone (page closed); nothing to dispose.
            }
        }
    }
}
