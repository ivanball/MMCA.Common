using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Components.Lists;

/// <summary>
/// The JS half <see cref="InfiniteScrollSentinel"/> and <see cref="MobileInfiniteScrollList{TItem}"/>
/// share: the <c>_content/MMCA.Common.UI/infinite-scroll.js</c> module and one IntersectionObserver
/// that calls the owner's <c>OnSentinelVisible</c> when the sentinel nears the viewport. Every call
/// is best effort: a torn-down circuit or a call the browser refuses (the element already gone, say)
/// leaves the list at the pages already loaded instead of escaping the owner's render.
/// </summary>
/// <typeparam name="TOwner">The component whose <c>[JSInvokable] OnSentinelVisible</c> the observer calls.</typeparam>
/// <param name="js">The owner's JS runtime.</param>
/// <param name="owner">The component the observer reports to.</param>
internal sealed class InfiniteScrollObserver<TOwner>(IJSRuntime js, TOwner owner) : IAsyncDisposable
    where TOwner : class
{
    private const string ModulePath = "./_content/MMCA.Common.UI/infinite-scroll.js";

    private readonly string _observerId = Guid.NewGuid().ToString("N");

    // Single-flight import (the M72 pattern, L187): Blazor does not serialize OnAfterRenderAsync, so a
    // second ObserveAsync can start while the first import is in flight, and an unguarded "??=" then
    // imports twice and leaks the instance it overwrites.
    private readonly LazyJsModule _module = new(js, ModulePath);
    private DotNetObjectReference<TOwner>? _dotNetRef;
    private bool _disposed;

    /// <summary>Gets a value indicating whether an observer is attached.</summary>
    public bool IsObserving { get; private set; }

    /// <summary>
    /// Attaches (or re-attaches, which yields a fresh initial intersection entry) the observer to
    /// <paramref name="sentinel"/>, importing the module on first use.
    /// </summary>
    /// <param name="sentinel">The sentinel element to watch.</param>
    /// <returns>A task that completes when the attempt is over, whatever its outcome.</returns>
    public async Task ObserveAsync(ElementReference sentinel)
    {
        try
        {
            await _module.GetOrImportAsync();

            // The owner can be disposed while the import is in flight (a host stops rendering the
            // sentinel the moment the last page arrives). DisposeAsync then found no module to
            // release, so the late one is released here and no observer is attached that nobody
            // would ever detach.
            if (_disposed)
            {
                await _module.DisposeAsync();
                return;
            }

            // Re-read after the check: the shared import has completed, so this is the cached module.
            var module = await _module.GetOrImportAsync();
            _dotNetRef ??= DotNetObjectReference.Create(owner);
            await module.InvokeVoidAsync("observe", _dotNetRef, sentinel, _observerId);
            IsObserving = true;
        }
        catch (JSDisconnectedException)
        {
            // Prerendering or circuit teardown: the list stops at the pages already loaded.
        }
        catch (JSException)
        {
            // The browser refused the call: the list stops at the pages already loaded.
        }
    }

    /// <summary>Detaches the observer, if one is attached.</summary>
    /// <returns>A task that completes when the attempt is over, whatever its outcome.</returns>
    public async Task UnobserveAsync()
    {
        if (!_module.IsImported || !IsObserving)
        {
            return;
        }

        try
        {
            var module = await _module.GetOrImportAsync();
            await module.InvokeVoidAsync("unobserve", _observerId);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to detach.
        }
        catch (JSException)
        {
            // Best effort, like the observe.
        }

        IsObserving = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await UnobserveAsync();
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to release.
        }
        catch (JSException)
        {
            // Best effort: ignore shutdown-time interop races.
        }
        finally
        {
            _dotNetRef?.Dispose();
        }
    }
}
