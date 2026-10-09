using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

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
    private IJSObjectReference? _module;
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
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);

            // The owner can be disposed while the import is in flight (a host stops rendering the
            // sentinel the moment the last page arrives). DisposeAsync then found no module to
            // release, so the late one is released here and no observer is attached that nobody
            // would ever detach.
            if (_disposed)
            {
                var late = _module;
                _module = null;
                await late.DisposeAsync();
                return;
            }

            _dotNetRef ??= DotNetObjectReference.Create(owner);
            await _module.InvokeVoidAsync("observe", _dotNetRef, sentinel, _observerId);
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
        if (_module is null || !IsObserving)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("unobserve", _observerId);
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

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
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
