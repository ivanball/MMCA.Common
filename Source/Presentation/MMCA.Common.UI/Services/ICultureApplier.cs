namespace MMCA.Common.UI.Services;

/// <summary>
/// Applies a culture switch for the current head (ADR-027). The mechanism is host-specific and must
/// not be hard-coded by the UI: a Blazor Web head round-trips through the server
/// <c>/culture/set</c> endpoint so the cookie, SSR prerender, and the WASM runtime all agree, while a
/// MAUI Blazor Hybrid head has no ASP.NET pipeline at all and switches the process culture in place.
/// <para>
/// Implementations own landing the user back on the return path: on the web that is the endpoint's
/// redirect, on a hybrid head it is a WebView reload. Callers must therefore treat
/// <c>ApplyAsync</c> as terminal and do no further navigation of their own.
/// </para>
/// </summary>
public interface ICultureApplier
{
    /// <summary>
    /// Switches the active culture and returns the user to <paramref name="returnPath"/>.
    /// </summary>
    /// <param name="culture">
    /// The culture to activate (e.g. <c>"es"</c>). Values outside
    /// <c>SupportedCultures.All</c> are ignored by the underlying mechanism rather than throwing.
    /// </param>
    /// <param name="returnPath">
    /// The app-relative path (with query) to land on afterwards. Empty falls back to <c>"/"</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyAsync(string culture, string returnPath, CancellationToken cancellationToken = default);
}
