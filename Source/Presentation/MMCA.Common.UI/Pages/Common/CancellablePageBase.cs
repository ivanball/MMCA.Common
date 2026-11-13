using Microsoft.AspNetCore.Components;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Pages.Common;

/// <summary>
/// Base class for a page that owns a page-scoped <see cref="CancellationTokenSource"/>: it exposes
/// <see cref="PageToken"/>, cancelled when the page is disposed, and the dispose pattern around it.
/// <see cref="DetailPageBase"/> and <see cref="CreatePageBase"/> derive from it, so every page base
/// shares one cancellation contract instead of repeating the source and its dispose method.
/// </summary>
/// <remarks>
/// A page that owns further disposables overrides <see cref="Dispose(bool)"/>, releases its own
/// resources, and then calls the base, which cancels and disposes the source exactly once.
/// </remarks>
public abstract class CancellablePageBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// Gets a token cancelled when the page is disposed. Every awaited call the page makes takes it,
    /// so a navigation away (or an InteractiveAuto render-mode transition) cancels the in-flight
    /// work instead of completing into a component that is gone. Reading it after dispose returns an
    /// already-cancelled token and never throws.
    /// </summary>
    protected CancellationToken PageToken => _cts.LifetimeToken();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Cancels the page's in-flight work and releases the page-scoped cancellation source, once.
    /// Overrides release their own resources and then call the base.
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
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }
}
