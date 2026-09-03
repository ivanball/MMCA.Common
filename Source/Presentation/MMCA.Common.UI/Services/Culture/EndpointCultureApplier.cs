using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Services.Culture;

/// <summary>
/// Default <see cref="ICultureApplier"/> for Blazor Web heads (ADR-027): navigates to the server
/// <c>GET /culture/set</c> endpoint mapped by <c>MapCultureEndpoint()</c>, which writes the ASP.NET
/// culture cookie and local-redirects to <c>redirectUri</c>. The force-load is load-bearing: the server
/// re-renders SSR under the new cookie and the WASM runtime re-reads it on startup
/// (<see cref="MmcaCultureBootstrap"/>), keeping prerender and hydration on the same culture.
/// <para>
/// Only valid where that endpoint exists. A head with no ASP.NET pipeline (MAUI Blazor Hybrid) would
/// route the URL through the Blazor <c>Router</c> instead, which matches no page and renders the
/// not-found page, so those heads register their own applier after <c>AddUIShared</c>.
/// </para>
/// </summary>
/// <param name="navigation">The head's navigation manager.</param>
public sealed class EndpointCultureApplier(NavigationManager navigation) : ICultureApplier
{
    /// <inheritdoc />
    public Task ApplyAsync(string culture, string returnPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        var target = string.IsNullOrWhiteSpace(returnPath) ? "/" : returnPath;
        var url = $"/culture/set?culture={Uri.EscapeDataString(culture)}&redirectUri={Uri.EscapeDataString(target)}";

        // The endpoint validates the culture against the allowlist and ignores anything else, so an
        // unsupported value lands the user back on the same page unchanged rather than failing.
        navigation.NavigateTo(url, forceLoad: true);
        return Task.CompletedTask;
    }
}
