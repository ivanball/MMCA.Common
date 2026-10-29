using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.UI.Web.Pwa;

/// <summary>
/// Maps the framework service worker that makes a Blazor Web host an installable web app with an
/// offline page. The browser half is <c>_content/MMCA.Common.UI.Web/pwa-register.js</c>; the host
/// itself supplies the app identity (<c>manifest.webmanifest</c>, icons, <c>theme-color</c>).
/// </summary>
public static class ServiceWorkerEndpointExtensions
{
    /// <summary>The route the worker is served on. Root, so the worker's scope covers the whole site.</summary>
    public const string ServiceWorkerPath = "/service-worker.js";

    /// <summary>
    /// The configuration key that retires the worker. <see langword="false"/> serves an inert worker
    /// instead, which every installed client picks up on its next navigation.
    /// </summary>
    public const string EnabledKey = "Pwa:ServiceWorker:Enabled";

    private const string VersionToken = "__MMCA_SW_VERSION__";

    private static readonly Lazy<string> ActiveScript = new(() => ReadScript("service-worker.js"));

    private static readonly Lazy<string> RetiredScript = new(() => ReadScript("service-worker-retired.js"));

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps <c>GET /service-worker.js</c>: an anonymous script, excluded from the OpenAPI
        /// description and served with <c>Cache-Control: no-cache</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Never caches pages or data.</b> A server-rendered page carries the signed-in user's
        /// data, so the worker sends every navigation to the network and falls back to the offline
        /// page only when the network fails. It handles GET navigations alone: every other request
        /// (API calls, the Blazor circuit, hubs, framework files) reaches its fetch handler and is
        /// left to the browser's default network handling, and a navigation it handles goes to the
        /// network with the request's cookies as usual.
        /// </para>
        /// <para>
        /// <b>Updates with every release.</b> The package version is written into the script, so a
        /// new framework version is a byte change the browser installs on its next update check, and
        /// the new worker deletes the previous version's offline cache.
        /// </para>
        /// <para>
        /// <b>Kill switch.</b> Setting <see cref="EnabledKey"/> to <see langword="false"/> serves a
        /// worker with no fetch handler that deletes the offline cache. A bad worker stays on a
        /// visitor's device until a newer one replaces it, so retiring it is a configuration change,
        /// not a release.
        /// </para>
        /// </remarks>
        /// <returns>The endpoint's convention builder, for further conventions.</returns>
        public RouteHandlerBuilder MapCommonServiceWorker() =>
            endpoints.MapGet(ServiceWorkerPath, (HttpContext context, IConfiguration configuration) =>
                {
                    context.Response.Headers.CacheControl = "no-cache";
                    var script = configuration.GetValue(EnabledKey, defaultValue: true)
                        ? ActiveScript.Value
                        : RetiredScript.Value;
                    return Results.Text(script, "text/javascript", Encoding.UTF8);
                })
                .AllowAnonymous()
                .ExcludeFromDescription();
    }

    private static string ReadScript(string fileName)
    {
        var assembly = typeof(ServiceWorkerEndpointExtensions).Assembly;
        using var stream = assembly.GetManifestResourceStream($"MMCA.Common.UI.Web.Pwa.{fileName}")
            ?? throw new InvalidOperationException($"The embedded service worker script '{fileName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0";
        return reader.ReadToEnd().Replace(VersionToken, version, StringComparison.Ordinal);
    }
}
