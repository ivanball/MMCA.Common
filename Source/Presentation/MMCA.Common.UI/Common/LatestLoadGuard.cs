namespace MMCA.Common.UI.Common;

/// <summary>
/// Keeps a routed page showing the load it asked for last.
/// <para>
/// Blazor reuses a routed component instance across route-parameter changes, so a page that opens
/// entity 100 (slow response), then navigates to entity 101 (fast response), gets 100's late answer
/// after 101 has already rendered. Assigning it unconditionally leaves the URL on 101 while the page
/// holds 100, and any action bound to the loaded entity then fires against the wrong one.
/// </para>
/// <para>
/// This guard gives each load a generation and a cancellation token, cancelling the previous load as
/// the new one starts. Use it as a field on the page, disposed with the component:
/// </para>
/// <code>
/// private readonly LatestLoadGuard _load = new();
///
/// protected override async Task OnParametersSetAsync()
/// {
///     var (token, generation) = _load.Begin();
///     var result = await Service.GetByIdAsync(Id, token);
///     if (!_load.IsCurrent(generation))
///     {
///         return;
///     }
///
///     Order = result;
/// }
///
/// public void Dispose() => _load.Dispose();
/// </code>
/// <para>
/// <b>Not thread-safe by contract.</b> It is built for the renderer's synchronization context, where
/// component lifecycle methods and event callbacks are already serialized; do not share one instance
/// across threads.
/// </para>
/// </summary>
public sealed class LatestLoadGuard : IDisposable
{
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _disposed;

    /// <summary>
    /// Starts a new load: cancels and disposes the previous one, then hands back the token to pass
    /// to the fetch and the generation to check against once it returns.
    /// </summary>
    /// <returns>The new load's cancellation token and its generation.</returns>
    /// <exception cref="ObjectDisposedException">The guard has been disposed.</exception>
    public (CancellationToken Token, int Generation) Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancelAndDisposeCurrent();
        _cts = new CancellationTokenSource();
        _generation++;

        return (_cts.Token, _generation);
    }

    /// <summary>
    /// Reports whether <paramref name="generation"/> is still the latest load, i.e. whether its
    /// result may be assigned to the page.
    /// </summary>
    /// <param name="generation">The generation returned by the matching <see cref="Begin"/> call.</param>
    /// <returns><see langword="false"/> once a newer <see cref="Begin"/> has run, or after disposal.</returns>
    public bool IsCurrent(int generation) => !_disposed && generation == _generation;

    /// <summary>Cancels the in-flight load and releases the guard.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAndDisposeCurrent();
    }

    private void CancelAndDisposeCurrent()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }
}
