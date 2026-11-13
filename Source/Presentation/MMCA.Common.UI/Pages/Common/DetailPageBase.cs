using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Components.PageState;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Base class for a detail page: one aggregate, loaded by route id, edited inline. It owns the
/// pieces every such page otherwise repeats verbatim: the page token and dispose pattern (inherited
/// from <see cref="CancellablePageBase"/>), a <see cref="LatestLoadGuard"/>
/// for route-driven reloads, and the inline edit-mode lifecycle (<see cref="IsEditing"/> /
/// <see cref="IsDirty"/> plus the enter/leave transitions an unsaved-changes guard reads).
/// </summary>
/// <remarks>
/// <para>
/// A derived page keeps only the parts that differ per aggregate: how it loads (through its own
/// client service, guarded by <see cref="LoadGuard"/>), which fields it copies into its edit model,
/// and what a save posts. It calls <see cref="BeginEdit"/> / <see cref="EndEdit"/> at the
/// transitions so the dirty flag can never be left set behind a closed editor, and passes
/// <see cref="CancellablePageBase.PageToken"/> to every awaited call it makes outside the guarded load.
/// </para>
/// <para>
/// A page that owns further disposables overrides <see cref="Dispose(bool)"/> and calls the base
/// last.
/// </para>
/// </remarks>
public abstract class DetailPageBase : CancellablePageBase
{
    /// <summary>
    /// Gets the guard for the route-driven load: <c>Begin()</c> at the start of each load cancels the
    /// load it supersedes, and <c>IsCurrent(generation)</c> after each await tells a stale response
    /// to leave the page state alone. Disposed with the page.
    /// </summary>
    protected LatestLoadGuard LoadGuard { get; } = new();

    /// <summary>
    /// Gets where the route-driven load stands. A page renders <see cref="DetailLoadStateContent"/>
    /// for every state but <see cref="DetailLoadState.Loaded"/>, so a record that does not exist and
    /// a record that could not be fetched never share the same "not found" body.
    /// </summary>
    protected DetailLoadState LoadState { get; private set; } = DetailLoadState.Loading;

    /// <summary>
    /// Starts a route-driven load: supersedes the one in flight through <see cref="LoadGuard"/> and
    /// puts the page back in <see cref="DetailLoadState.Loading"/>.
    /// </summary>
    /// <returns>The token the load passes to its awaited calls and the generation it completes with.</returns>
    protected (CancellationToken Token, int Generation) BeginLoad()
    {
        var started = LoadGuard.Begin();
        LoadState = DetailLoadState.Loading;
        return started;
    }

    /// <summary>
    /// Records the outcome of the load started by <see cref="BeginLoad"/>, unless a newer load has
    /// superseded it: <see cref="DetailLoadState.Loaded"/> on success,
    /// <see cref="DetailLoadState.NotFound"/> for a not-found failure, and
    /// <see cref="DetailLoadState.Failed"/> for every other failure.
    /// </summary>
    /// <param name="generation">The generation <see cref="BeginLoad"/> returned.</param>
    /// <param name="result">The result of the page's primary fetch.</param>
    /// <returns>
    /// <see langword="true"/> when the load is still current and succeeded, so the page may adopt
    /// the value; <see langword="false"/> when it failed or was superseded.
    /// </returns>
    protected bool CompleteLoad(int generation, Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!LoadGuard.IsCurrent(generation))
        {
            return false;
        }

        if (result.IsSuccess)
        {
            LoadState = DetailLoadState.Loaded;
            return true;
        }

        LoadState = result.IsNotFound() ? DetailLoadState.NotFound : DetailLoadState.Failed;
        return false;
    }

    /// <summary>Gets a value indicating whether the inline editor is open.</summary>
    protected bool IsEditing { get; private set; }

    /// <summary>Gets a value indicating whether the reader changed a field in the open editor.</summary>
    protected bool IsDirty { get; private set; }

    /// <summary>Marks the open editor dirty. Bound to every editable field's <c>@bind-Value:after</c>.</summary>
    protected void MarkDirty() => IsDirty = true;

    /// <summary>
    /// Clears the dirty flag without opening or closing the editor. For a child panel that reports its
    /// own dirty state in both directions (true then false) while the page's editor state stays as it is.
    /// </summary>
    protected void ClearDirty() => IsDirty = false;

    /// <summary>Opens the inline editor on a clean slate.</summary>
    protected void BeginEdit()
    {
        IsDirty = false;
        IsEditing = true;
    }

    /// <summary>Closes the inline editor, clearing the dirty flag the navigation guard reads.</summary>
    protected void EndEdit()
    {
        IsDirty = false;
        IsEditing = false;
    }

    /// <summary>
    /// Releases the load guard and then lets the base cancel the page's in-flight work and release
    /// the page-scoped cancellation source. Overrides release their own resources and then call the base.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="CancellablePageBase.Dispose()"/>.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Idempotent, so a second Dispose stays a no-op.
            LoadGuard.Dispose();
        }

        base.Dispose(disposing);
    }
}
