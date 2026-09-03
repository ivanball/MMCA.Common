using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MMCA.Common.UI.Components.Lists;

/// <summary>
/// Bottom-of-list sentinel: raises <see cref="OnVisible"/> when it scrolls within 200px of the
/// viewport so the hosting page can fetch and append its next page. It drives the same
/// <c>_content/MMCA.Common.UI/infinite-scroll.js</c> IntersectionObserver module that
/// <see cref="MobileInfiniteScrollList{TItem}"/> uses, but only the observer: the item markup, the
/// fetch, and the accumulated list stay with the page. That split is what lets a page whose cards
/// are its own (a public card grid, say) get infinite scroll without giving up its layout, its empty
/// and error states, or the <c>DataGridListPageBase</c> mobile fetch path.
/// Owning the observer in a child component is also what makes the lifecycle correct: a page
/// deriving from <c>DataGridListPageBase</c> cannot hook async disposal (its <c>DisposeAsync</c> is
/// not virtual), while this component is disposed by the renderer the moment the host stops
/// rendering it, which is exactly when the last page has been loaded.
/// Render it ONLY while more pages exist; the host re-renders a fresh instance (and therefore a
/// fresh observer) after a filter reset refills the list.
/// </summary>
public partial class InfiniteScrollSentinel : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>Raised when the sentinel enters (or nears) the viewport.</summary>
    [Parameter] public EventCallback OnVisible { get; set; }

    /// <summary>True while the host is fetching the next page; renders the inline progress row.</summary>
    [Parameter] public bool IsLoading { get; set; }

    /// <summary>Accessible name for the progress row (ADR-027: localized by the host).</summary>
    [Parameter] public string? LoadingLabel { get; set; }

    private readonly string _observerId = Guid.NewGuid().ToString("N");
    private ElementReference _sentinelRef;
    private IJSObjectReference? _module;
    private DotNetObjectReference<InfiniteScrollSentinel>? _dotNetRef;
    private bool _observing;
    private bool _disposed;

    /// <summary>
    /// Invoked from JS when the IntersectionObserver reports the sentinel visible. The name is
    /// fixed by the shared <c>infinite-scroll.js</c> module, which calls it by string.
    /// </summary>
    [JSInvokable]
    public Task OnSentinelVisible() =>
        _disposed ? Task.CompletedTask : InvokeAsync(() => OnVisible.InvokeAsync());

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_disposed)
        {
            await AttachObserverAsync();
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_module is not null)
            {
                if (_observing)
                {
                    await _module.InvokeVoidAsync("unobserve", _observerId);
                }

                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to detach.
        }
        catch (JSException)
        {
            // Best-effort: ignore shutdown-time interop races.
        }
        finally
        {
            _dotNetRef?.Dispose();
        }
    }

    private async Task AttachObserverAsync()
    {
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/MMCA.Common.UI/infinite-scroll.js");
            _dotNetRef ??= DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("observe", _dotNetRef, _sentinelRef, _observerId);
            _observing = true;
        }
        catch (JSDisconnectedException)
        {
            // Expected during prerendering or circuit teardown: the list then simply stops at the
            // pages already loaded rather than failing the render.
        }
    }
}
