using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.UI.Web.Hosting;

/// <summary>
/// The one render-mode selector a Blazor head's <c>App.razor</c> uses, so every head honours the two
/// E2E switches the AppHost forwards (<c>WithE2eForceWebAssembly</c> and <c>WithE2eForceServer</c> in
/// MMCA.Common.Aspire.Hosting) with the same precedence.
/// </summary>
public static class E2eRenderMode
{
    /// <summary>
    /// The configuration key that pins the head to InteractiveWebAssembly (the AppHost writes it as
    /// <c>E2E__ForceWebAssembly</c>). Wins over <see cref="ForceServerKey"/>.
    /// </summary>
    public const string ForceWebAssemblyKey = "E2E:ForceWebAssembly";

    /// <summary>
    /// The configuration key that pins the head to InteractiveServer (the AppHost writes it as
    /// <c>E2E__ForceServer</c>).
    /// </summary>
    public const string ForceServerKey = "E2E:ForceServer";

    /// <summary>
    /// Resolves the head's render mode: InteractiveWebAssembly when <see cref="ForceWebAssemblyKey"/> is
    /// <see langword="true"/>, otherwise InteractiveServer when <see cref="ForceServerKey"/> is
    /// <see langword="true"/>, otherwise InteractiveAuto (what production and local development see).
    /// </summary>
    /// <param name="configuration">The head's configuration.</param>
    /// <returns>The render mode to apply to the routes and the head outlet.</returns>
    public static IComponentRenderMode Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetValue<bool>(ForceWebAssemblyKey))
        {
            return RenderMode.InteractiveWebAssembly;
        }

        return configuration.GetValue<bool>(ForceServerKey)
            ? RenderMode.InteractiveServer
            : RenderMode.InteractiveAuto;
    }

    /// <summary>
    /// Resolves the render mode for the current request: <see langword="null"/> (static server
    /// rendering) when the matched page opts out of interactive routing with
    /// <see cref="ExcludeFromInteractiveRoutingAttribute"/>, as the framework <c>/Error</c> page does,
    /// otherwise <see cref="Resolve(IConfiguration)"/>. A head that applies this to its routes and head
    /// outlet keeps such a page static instead of handing it to an interactive router that does not
    /// know it (which answered it with the not-found page).
    /// </summary>
    /// <param name="configuration">The head's configuration.</param>
    /// <param name="httpContext">The current request: the <c>App</c> component's cascading <see cref="HttpContext"/>.</param>
    /// <returns>The render mode to apply, or <see langword="null"/> to render the request statically.</returns>
    public static IComponentRenderMode? Resolve(IConfiguration configuration, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.AcceptsInteractiveRouting() ? Resolve(configuration) : null;
    }
}
