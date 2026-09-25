using Microsoft.AspNetCore.Components;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Base class for a detail page: one aggregate, loaded by route id, edited inline. It owns the
/// pieces every such page otherwise repeats verbatim: the page-scoped
/// <see cref="CancellationTokenSource"/> with its dispose pattern, a <see cref="LatestLoadGuard"/>
/// for route-driven reloads, and the inline edit-mode lifecycle (<see cref="IsEditing"/> /
/// <see cref="IsDirty"/> plus the enter/leave transitions an unsaved-changes guard reads).
/// </summary>
/// <remarks>
/// <para>
/// A derived page keeps only the parts that differ per aggregate: how it loads (through its own
/// client service, guarded by <see cref="LoadGuard"/>), which fields it copies into its edit model,
/// and what a save posts. It calls <see cref="BeginEdit"/> / <see cref="EndEdit"/> at the
/// transitions so the dirty flag can never be left set behind a closed editor, and passes
/// <see cref="PageToken"/> to every awaited call it makes outside the guarded load.
/// </para>
/// <para>
/// A page that owns further disposables overrides <see cref="Dispose(bool)"/> and calls the base
/// last.
/// </para>
/// </remarks>
public abstract class DetailPageBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// Gets a token cancelled when the page is disposed. Every awaited call the page makes takes it,
    /// so a navigation away (or an InteractiveAuto render-mode transition) cancels the in-flight
    /// work instead of completing into a component that is gone.
    /// </summary>
    protected CancellationToken PageToken => _cts.Token;

    /// <summary>
    /// Gets the guard for the route-driven load: <c>Begin()</c> at the start of each load cancels the
    /// load it supersedes, and <c>IsCurrent(generation)</c> after each await tells a stale response
    /// to leave the page state alone. Disposed with the page.
    /// </summary>
    protected LatestLoadGuard LoadGuard { get; } = new();

    /// <summary>Gets a value indicating whether the inline editor is open.</summary>
    protected bool IsEditing { get; private set; }

    /// <summary>Gets a value indicating whether the reader changed a field in the open editor.</summary>
    protected bool IsDirty { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Marks the open editor dirty. Bound to every editable field's <c>@bind-Value:after</c>.</summary>
    protected void MarkDirty() => IsDirty = true;

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
    /// Cancels the page's in-flight work and releases the page-scoped cancellation source and load
    /// guard. Overrides release their own resources and then call the base.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            LoadGuard.Dispose();
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }
}
