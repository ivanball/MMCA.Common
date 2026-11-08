using System.Text.Json;
using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Capabilities.Auth;

/// <summary>
/// Browser <see cref="IPasskeyCapability"/> over <c>navigator.credentials</c>, through the
/// <c>passkeys.js</c> module (ADR-139). Registered by <c>AddBrowserDeviceCapabilities</c> for the Blazor
/// Server and WebAssembly heads. Every call is prerender-safe: with JS interop unavailable, or when the
/// user cancels the browser prompt, it answers <see langword="null"/> (or <see langword="false"/>) and
/// the caller shows its own message.
/// </summary>
public sealed class BrowserPasskeyCapability : IPasskeyCapability, IAsyncDisposable
{
    private const string ModulePath = "./_content/MMCA.Common.UI/passkeys.js";

    // The browser prompt waits on the person (finding the security key, reading the phone), so the
    // call carries its own generous limit instead of the circuit's default interop timeout.
    private static readonly TimeSpan CeremonyTimeout = TimeSpan.FromMinutes(5);

    private readonly LazyJsModule _module;
    private DotNetObjectReference<AutofillCallback>? _autofillCallback;

    /// <summary>Initializes the capability over the host's <see cref="IJSRuntime"/>.</summary>
    /// <param name="jsRuntime">The JS runtime of the circuit or the WebAssembly app.</param>
    public BrowserPasskeyCapability(IJSRuntime jsRuntime) => _module = new LazyJsModule(jsRuntime, ModulePath);

    /// <inheritdoc />
    /// <remarks>
    /// Always true on a browser head: whether this particular browser supports WebAuthn is checked by
    /// the module at the moment of the ceremony, which then answers <see langword="null"/>.
    /// </remarks>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public Task<JsonElement?> CreateAsync(JsonElement options, CancellationToken cancellationToken = default) =>
        InvokeCeremonyAsync("create", options, cancellationToken);

    /// <inheritdoc />
    public Task<JsonElement?> GetAsync(JsonElement options, CancellationToken cancellationToken = default) =>
        InvokeCeremonyAsync("get", options, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> StartAutofillAsync(JsonElement options, Func<JsonElement, Task> onCredential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCredential);

        _autofillCallback?.Dispose();
        _autofillCallback = DotNetObjectReference.Create(new AutofillCallback(onCredential));
        try
        {
            var module = await _module.GetOrImportAsync(cancellationToken).ConfigureAwait(false);
            return await module.InvokeAsync<bool>("startAutofill", cancellationToken, options, _autofillCallback).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task CancelAutofillAsync()
    {
        if (!_module.IsImported)
        {
            return;
        }

        try
        {
            var module = await _module.GetOrImportAsync().ConfigureAwait(false);
            await module.InvokeVoidAsync("cancelAutofill").ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            // Nothing pending can be reached any more; nothing to cancel.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _autofillCallback?.Dispose();
        _autofillCallback = null;
        await _module.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsInteropUnavailable(Exception exception) =>
        exception is InvalidOperationException or JSDisconnectedException or JSException or TaskCanceledException;

    private async Task<JsonElement?> InvokeCeremonyAsync(string identifier, JsonElement options, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CeremonyTimeout);
        try
        {
            var module = await _module.GetOrImportAsync(limit.Token).ConfigureAwait(false);
            return await module.InvokeAsync<JsonElement?>(identifier, limit.Token, options).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInteropUnavailable(exception) && !cancellationToken.IsCancellationRequested)
        {
            // Prerender, a torn-down circuit, a browser that throws, or the prompt outliving its limit:
            // the same "no credential" answer as a cancelled prompt.
            return null;
        }
    }

    /// <summary>The .NET target <c>passkeys.js</c> calls when the user picks a passkey from autofill.</summary>
    /// <param name="onCredential">Receives the assertion.</param>
    internal sealed class AutofillCallback(Func<JsonElement, Task> onCredential)
    {
        /// <summary>Called from JS with the assertion of the passkey the user picked.</summary>
        /// <param name="credential">The assertion (the AuthenticationResponseJSON shape).</param>
        /// <returns>The handler's task.</returns>
        [JSInvokable]
        public Task OnAutofillCredential(JsonElement credential) => onCredential(credential);
    }
}
