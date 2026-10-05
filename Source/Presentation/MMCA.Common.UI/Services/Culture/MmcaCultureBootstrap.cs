using System.Globalization;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.UI.Services.Culture;

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
    /// The pseudo locale is never accepted; a Development host that wants it uses
    /// <see cref="SetBrowserCultureAsync(IJSRuntime, bool)"/>.
    /// </summary>
    /// <param name="jsRuntime">The WASM host's JS runtime (resolve from <c>host.Services</c>).</param>
    /// <returns>A task that completes once the thread default cultures are set.</returns>
    public static Task SetBrowserCultureAsync(IJSRuntime jsRuntime) =>
        SetBrowserCultureAsync(jsRuntime, allowPseudoLocale: false);

    /// <summary>
    /// Resolves the culture from the browser's culture cookie exactly as
    /// <see cref="SetBrowserCultureAsync(IJSRuntime)"/> does, additionally keeping the
    /// <see cref="SupportedCultures.PseudoLocale"/> when <paramref name="allowPseudoLocale"/> is
    /// <see langword="true"/>. The server accepts the pseudo locale only in Development
    /// (<c>MapCultureEndpoint</c> and request localization), so pass
    /// <c>builder.HostEnvironment.IsDevelopment()</c> from the WASM <c>Program.cs</c>: hydration then
    /// stays in the pseudo locale the server prerendered, and the pseudo localizer registered by
    /// <c>AddUIShared</c> transforms the client's strings too.
    /// </summary>
    /// <param name="jsRuntime">The WASM host's JS runtime (resolve from <c>host.Services</c>).</param>
    /// <param name="allowPseudoLocale">Whether the pseudo locale is a culture this host may run in.</param>
    /// <returns>A task that completes once the thread default cultures are set.</returns>
    public static async Task SetBrowserCultureAsync(IJSRuntime jsRuntime, bool allowPseudoLocale)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);

        await using var module = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/MMCA.Common.UI/culture.js");
        var culture = await module.InvokeAsync<string?>("getCulture");

        var accepted = SupportedCultures.IsSupported(culture)
            || allowPseudoLocale && SupportedCultures.IsPseudoLocale(culture);
        var resolved = accepted ? culture! : SupportedCultures.Default;
        var cultureInfo = new CultureInfo(resolved);
        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
    }
}
