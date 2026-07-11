using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Capabilities.Browser;

/// <summary>
/// Lazy, prerender-safe accessor for <c>capabilities-interop.js</c>, shared by the browser
/// capability implementations (one module import per scope/circuit). Every invocation
/// swallows the JS-unavailable exception family — prerender, disconnected circuit, or a
/// throwing browser API — and returns <see langword="default"/>, mirroring
/// <c>MauiBackNavigationBridge</c>'s degradation contract.
/// </summary>
public sealed class CapabilitiesJsModule : IAsyncDisposable
{
    private const string ModulePath = "./_content/MMCA.Common.UI/capabilities-interop.js";

    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;

    /// <summary>Initializes the accessor over the host's <see cref="IJSRuntime"/>.</summary>
    public CapabilitiesJsModule(IJSRuntime jsRuntime) => _jsRuntime = jsRuntime;

    /// <summary>
    /// Invokes a module export, returning <see langword="default"/> when JS interop is
    /// unavailable (SSR prerender, torn-down circuit) or the call throws in the browser.
    /// </summary>
    /// <typeparam name="T">The JSON-deserializable return type of the module export.</typeparam>
    public async ValueTask<T?> InvokeOrDefaultAsync<T>(
        string identifier,
        object?[] args,
        CancellationToken cancellationToken)
    {
        try
        {
            _module ??= await _jsRuntime
                .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
                .ConfigureAwait(false);
            return await _module.InvokeAsync<T>(identifier, cancellationToken, args).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // JS interop unavailable (Blazor not yet hydrated / SSR prerender).
            return default;
        }
        catch (JSDisconnectedException)
        {
            return default;
        }
        catch (JSException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.DisposeAsync().ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to release.
        }
    }
}
