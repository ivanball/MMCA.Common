using Microsoft.JSInterop;

namespace MMCA.Common.UI.Theme;

/// <summary>
/// The guard the theme components' best-effort JS interop calls share: the root layout and the
/// toggle must never let a JS failure escape a component lifecycle and kill the circuit.
/// </summary>
internal static class ThemeInterop
{
    /// <summary>Runs one best-effort interop call.</summary>
    /// <param name="call">The interop call.</param>
    /// <returns>
    /// <see langword="false"/> when it failed: the asset is missing or the call failed in the
    /// browser (<see cref="JSException"/>), the circuit was torn down mid-call
    /// (<see cref="JSDisconnectedException"/>), or interop is unavailable on this renderer
    /// (<see cref="InvalidOperationException"/>: a prerender race or a disposed dispatcher).
    /// </returns>
    internal static async Task<bool> TryAsync(Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            return false;
        }
    }
}
