using System.Globalization;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Blazor WebAssembly culture bootstrap (ADR-027). Reads the same ASP.NET culture cookie the server used
/// during SSR prerender and sets the thread default cultures <b>before</b> the WASM host runs, so the
/// interactive client renders in the same language the server prerendered — no locale flash and no
/// prerender/hydration mismatch. Call from the <c>.Client</c> <c>Program.cs</c> after
/// <c>builder.Build()</c> and before <c>host.RunAsync()</c>.
/// </summary>
public static class MmcaCultureBootstrap
{
    /// <summary>
    /// Resolves the culture from the browser's culture cookie (falling back to
    /// <see cref="SupportedCultures.Default"/>) and assigns it to
    /// <see cref="CultureInfo.DefaultThreadCurrentCulture"/> / <see cref="CultureInfo.DefaultThreadCurrentUICulture"/>.
    /// </summary>
    /// <param name="jsRuntime">The WASM host's JS runtime (resolve from <c>host.Services</c>).</param>
    public static async Task SetBrowserCultureAsync(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);

        await using var module = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/MMCA.Common.UI/culture.js");
        var culture = await module.InvokeAsync<string?>("getCulture");

        var resolved = SupportedCultures.IsSupported(culture) ? culture! : SupportedCultures.Default;
        var cultureInfo = new CultureInfo(resolved);
        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
    }
}
