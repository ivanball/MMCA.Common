using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Single-flight, prerender-safe importer for one JS module, shared by the UI services that own a
/// <c>_content/MMCA.Common.UI/*.js</c> module. An unguarded <c>_module ??= await import(...)</c>
/// lets two concurrent callers each start an import; the browser then holds two module instances
/// and the later assignment leaks the earlier reference (it is never disposed). This class caches
/// the in-flight import task under a lock so concurrent callers share ONE import, and drops that
/// task when it fails so a later call retries: an import attempted during SSR prerender must not
/// poison the module for the rest of the circuit.
/// </summary>
/// <remarks>
/// Callers keep their own exception handling: this class deliberately does not swallow anything on
/// the import path, so each service can keep its own degradation contract (return default, fall
/// back to a navigation, no-op). Disposal is guarded, because a torn-down circuit is the normal
/// end of life for a scoped UI service.
/// </remarks>
internal sealed class LazyJsModule(IJSRuntime js, string modulePath) : IAsyncDisposable
{
    private readonly Lock _sync = new();

    private Task<IJSObjectReference>? _inFlight;
    private IJSObjectReference? _module;

    /// <summary>Whether the module has been imported (used to skip work on disposal).</summary>
    public bool IsImported => _module is not null;

    /// <summary>
    /// Returns the imported module, importing it on first use. Concurrent callers share one import.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the import call.</param>
    public async Task<IJSObjectReference> GetOrImportAsync(CancellationToken cancellationToken = default)
    {
        if (_module is { } cached)
        {
            return cached;
        }

        // The lock is what makes the import single: ImportAsync reaches its first await
        // immediately, so nothing slow runs under it.
        Task<IJSObjectReference> inFlight;
        lock (_sync)
        {
            _inFlight ??= ImportAsync(cancellationToken);
            inFlight = _inFlight;
        }

        try
        {
            return await inFlight.ConfigureAwait(false);
        }
        finally
        {
            // Only clear a FAILED task, and only our own: clearing unconditionally could drop a
            // newer import started after this one completed, splitting the next set of callers.
            if (!inFlight.IsCompletedSuccessfully)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_inFlight, inFlight))
                    {
                        _inFlight = null;
                    }
                }
            }
        }
    }

    private async Task<IJSObjectReference> ImportAsync(CancellationToken cancellationToken)
    {
        var module = await js
            .InvokeAsync<IJSObjectReference>("import", cancellationToken, modulePath)
            .ConfigureAwait(false);

        _module = module;
        return module;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not { } module)
        {
            return;
        }

        _module = null;

        try
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to release.
        }
    }
}
