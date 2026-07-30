using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Maui.Globalization;

/// <summary>
/// <see cref="ICultureApplier"/> for MAUI Blazor Hybrid heads (ADR-027). The web default
/// (<c>EndpointCultureApplier</c>) navigates to the server <c>/culture/set</c> endpoint; a hybrid head
/// has no ASP.NET pipeline, so that URL is resolved by the Blazor <c>Router</c> instead, matches no
/// page, and renders the not-found page. This applier switches the culture in process and reloads the
/// WebView instead.
/// <para>
/// The reload is what makes the change visible: resource strings are resolved from
/// <c>CultureInfo.CurrentUICulture</c> at render time, and Blazor has no API to re-render an entire
/// component tree in place. A force-load in a <c>BlazorWebView</c> re-boots the Blazor app inside the
/// WebView while the .NET process (and therefore the culture set just above) stays alive, so every
/// component re-renders under the new culture and the user lands back on the return path.
/// </para>
/// </summary>
/// <param name="navigation">The hybrid head's navigation manager.</param>
public sealed class MauiCultureApplier(NavigationManager navigation) : ICultureApplier
{
    /// <inheritdoc />
    public Task ApplyAsync(string culture, string returnPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        // Parity with the web endpoint, which honors only allowlisted cultures and otherwise returns the
        // user to the same page unchanged. The pseudo locale is never reachable here: the switcher only
        // offers it when IHostEnvironment reports Development, and a MAUI head registers no such service.
        if (!SupportedCultures.IsSupported(culture))
        {
            return Task.CompletedTask;
        }

        // Order is load-bearing: persist and activate BEFORE the reload, so the new culture is already
        // the process default when the tree re-renders. ApplyToProcess deliberately sets only the thread
        // defaults; see its remarks for why assigning CurrentUICulture here would pin the app to its
        // startup language for the rest of the session.
        MauiCultureStore.Save(culture);
        MauiCultureStore.ApplyToProcess(culture);

        var target = string.IsNullOrWhiteSpace(returnPath) ? "/" : returnPath;
        navigation.NavigateTo(target, forceLoad: true);

        return Task.CompletedTask;
    }
}
