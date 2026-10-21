namespace MMCA.Common.UI.Common.Interfaces;

/// <summary>
/// A toast that outlives a full page reload. <see cref="IToastService"/> renders into the current
/// circuit or WebAssembly runtime, so a toast raised immediately before
/// <c>NavigationManager.NavigateTo(url, forceLoad: true)</c> is destroyed with the page that raised
/// it. Queue it here instead: the message is parked in the browser's <c>sessionStorage</c> and the
/// shared layout's <c>PendingToastHost</c> shows it once on the first interactive render of the
/// reloaded page.
/// <para>
/// One toast is held at a time; queueing again replaces the pending one. Storage failures
/// (prerender, a torn-down circuit, a browser that refuses <c>sessionStorage</c>) are swallowed,
/// because a lost confirmation toast must never break the action that raised it.
/// </para>
/// </summary>
public interface IPendingToastService
{
    /// <summary>
    /// Parks a toast to be shown after the next full page load. Call it, await it, then navigate
    /// with <c>forceLoad: true</c>.
    /// </summary>
    /// <param name="severity">The level to render at.</param>
    /// <param name="message">The already-localized sentence to show.</param>
    /// <param name="ct">
    /// Cancels the storage write. Do not pass the page's lifetime token when the same action signs
    /// the user out of an <c>[Authorize]</c> page: the sign-out re-renders the route into the login
    /// redirect and disposes the page, which cancels that token and silently drops the toast. Pass
    /// <see cref="CancellationToken.None"/> (or nothing) there.
    /// </param>
    /// <returns>A task that completes once the toast is stored (or the write was abandoned).</returns>
    ValueTask QueueAsync(ToastSeverity severity, string message, CancellationToken ct = default);

    /// <summary>
    /// Reads and removes the parked toast, if any. Called by <c>PendingToastHost</c>; a host that
    /// renders its own layout calls it once after its first interactive render.
    /// </summary>
    /// <param name="ct">Cancels the storage read.</param>
    /// <returns>The parked toast, or <see langword="null"/> when none is waiting or storage is unavailable.</returns>
    ValueTask<(ToastSeverity Severity, string Message)?> TakeAsync(CancellationToken ct = default);
}
