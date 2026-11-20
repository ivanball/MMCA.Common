using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Web.SameOriginProxy;
using MMCA.Common.UI.Web.Security;

namespace MMCA.Common.UI.Web.ClientConfig;

/// <summary>
/// Maps the runtime configuration document a Blazor WebAssembly client fetches at startup, so the
/// API base address is not baked into the static bundle. The client half is
/// <c>MmcaClientConfigBootstrap.LoadAsync</c> in <c>MMCA.Common.UI</c>.
/// </summary>
public static class ClientConfigEndpointExtensions
{
    /// <summary>The route the document is served on.</summary>
    public const string ClientConfigPath = "/client-config";

    /// <summary>The framework section carrying the browser-reachable API base address.</summary>
    public const string ApiSectionName = "Api";

    /// <summary>
    /// The framework section carrying the head's resolved module switches, so the WebAssembly client
    /// registers exactly the UI modules the server head does.
    /// </summary>
    public const string ModulesSectionName = "Modules";

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps <c>GET /client-config</c>: an anonymous document of the shape
        /// <c>{ "api": { "apiEndpoint": "&lt;Api:WasmApiEndpoint&gt;" }, "modules": { "Identity": { "enabled": true } }, "layout": { ... }, ...extras }</c>,
        /// excluded from the OpenAPI description.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Fail-closed.</b> The endpoint serves <c>Api:WasmApiEndpoint</c> (the browser-reachable
        /// gateway URL) and never falls back to <c>Api:ApiEndpoint</c>: that value is the server
        /// head's service-discovery name, which a browser cannot resolve, so serving it would hand the
        /// client a silently broken base address. A host without the key, or whose value is not an
        /// absolute http(s) URL (a deployment placeholder, for one), answers every request with a
        /// server error whose exception names the key, which the client's bootstrap then surfaces as a
        /// failed start instead of a client pointed at the wrong API.
        /// </para>
        /// <para>
        /// <b>Modules forwarded.</b> The <c>modules</c> section lists every entry of the head's own
        /// <c>Modules</c> configuration with ONLY its resolved enabled flag
        /// (<c>UIModuleConfiguration.IsModuleEnabled</c>), never its other settings. The WebAssembly
        /// client loads the document into configuration before it registers modules, so a module the
        /// head disables does not come back after hydration. A head with no <c>Modules</c> section serves
        /// an empty one.
        /// </para>
        /// <para>
        /// <b>Common.UI settings forwarded.</b> The <c>layout</c>, <c>registration</c>, <c>signIn</c>
        /// and <c>legal</c> sections carry the server head's <c>Layout</c>, <c>Registration</c>,
        /// <c>SignIn</c> and <c>Legal</c> configuration, limited to the properties the matching
        /// Common.UI settings class declares and to the keys the server actually sets; a section the
        /// server does not configure is left out, so the client's defaults hold. A section the host
        /// adds through <paramref name="extras"/> under the same name replaces the forwarded one.
        /// </para>
        /// <para>
        /// <b>Anonymous by declaration.</b> The WASM client fetches the document before it can hold a
        /// session, and a host's fallback authorization policy gates any endpoint that states nothing,
        /// so the endpoint carries <c>AllowAnonymous</c> itself. Only add values through
        /// <paramref name="extras"/> that the public site renders anyway.
        /// </para>
        /// </remarks>
        /// <param name="extras">
        /// Adds the host's own sections (sign-in provider flags, public contact details) per request.
        /// </param>
        /// <returns>The endpoint's convention builder, for further conventions.</returns>
        public RouteHandlerBuilder MapClientConfigEndpoint(Action<ClientConfigBuilder>? extras = null) =>
            endpoints.MapGet(ClientConfigPath, (HttpContext context, IOptions<ApiSettings> apiSettings) =>
                {
                    var wasmApiEndpoint = apiSettings.Value.WasmApiEndpoint;
                    if (!ApiEndpointUrl.IsAbsoluteHttp(wasmApiEndpoint))
                    {
                        throw new InvalidOperationException(
                            "Api:WasmApiEndpoint is not configured. The WASM client needs the browser-reachable "
                            + "gateway URL; Api:ApiEndpoint is the server head's service-discovery name and cannot stand in. "
                            + "The value must be an absolute http(s) URL (for example https://gateway.example.com), "
                            + "not blank and not a placeholder.");
                    }

                    var builder = new ClientConfigBuilder(context);
                    extras?.Invoke(builder);

                    // Section names go through the same camelCase policy the web JSON defaults apply to
                    // members, so the document keeps the shape an anonymous-object payload produced.
                    var document = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [JsonNamingPolicy.CamelCase.ConvertName(ApiSectionName)] = BuildApiSection(context, wasmApiEndpoint),
                        [JsonNamingPolicy.CamelCase.ConvertName(ModulesSectionName)] = BuildModulesSection(builder.Configuration),
                    };

                    // Common.UI's own Layout, Registration, SignIn and Legal sections, as far as the
                    // server sets them; written before the host's extras so a head that still adds one
                    // of them itself replaces the forwarded copy rather than colliding with it.
                    foreach (var (name, value) in ClientConfigUiSections.Build(builder.Configuration))
                    {
                        document[name] = value;
                    }

                    foreach (var (name, value) in builder.Sections)
                    {
                        document[JsonNamingPolicy.CamelCase.ConvertName(name)] = value;
                    }

                    return Results.Ok(document);
                })
                .AllowAnonymous()
                .ExcludeFromDescription();
    }

    /// <summary>
    /// The framework's <c>Modules</c> section: each module the head's configuration names, keyed as it
    /// is spelled there, with only its resolved enabled flag.
    /// </summary>
    private static Dictionary<string, object> BuildModulesSection(IConfiguration configuration) =>
        configuration.GetSection(ModulesSectionName).GetChildren().ToDictionary(
            module => module.Key,
            module => (object)new { Enabled = UIModuleConfiguration.IsModuleEnabled(configuration, module.Key) },
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The framework's <c>Api</c> section. A host running the same-origin API proxy
    /// (<c>AddCommonSameOriginApiProxy</c>) also serves <c>sameOriginApiEndpoint</c>, the proxy's
    /// origin-relative base (for example <c>/api/</c>), which the WASM bootstrap resolves against the
    /// page's own origin; <c>apiEndpoint</c> stays the gateway for full-page navigations. Every other
    /// host serves the section exactly as before.
    /// </summary>
    private static object BuildApiSection(HttpContext context, string wasmApiEndpoint)
    {
        if (context.RequestServices.GetService<SameOriginApiProxyMarker>() is null)
        {
            return new { ApiEndpoint = wasmApiEndpoint };
        }

        var prefix = context.RequestServices.GetRequiredService<IOptions<SameOriginApiProxySettings>>().Value.PathPrefix;
        return new { ApiEndpoint = wasmApiEndpoint, SameOriginApiEndpoint = prefix + "/" };
    }
}
