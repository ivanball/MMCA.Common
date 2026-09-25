using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;

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

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps <c>GET /client-config</c>: an anonymous document of the shape
        /// <c>{ "api": { "apiEndpoint": "&lt;Api:WasmApiEndpoint&gt;" }, ...extras }</c>, excluded
        /// from the OpenAPI description.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Fail-closed.</b> The endpoint serves <c>Api:WasmApiEndpoint</c> (the browser-reachable
        /// gateway URL) and never falls back to <c>Api:ApiEndpoint</c>: that value is the server
        /// head's service-discovery name, which a browser cannot resolve, so serving it would hand the
        /// client a silently broken base address. A host without the key answers every request with a
        /// server error whose exception names the key, which the client's bootstrap then surfaces as a
        /// failed start instead of a client pointed at the wrong API.
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
                    if (string.IsNullOrWhiteSpace(wasmApiEndpoint))
                    {
                        throw new InvalidOperationException(
                            "Api:WasmApiEndpoint is not configured. The WASM client needs the browser-reachable "
                            + "gateway URL; Api:ApiEndpoint is the server head's service-discovery name and cannot stand in.");
                    }

                    var builder = new ClientConfigBuilder(context);
                    extras?.Invoke(builder);

                    // Section names go through the same camelCase policy the web JSON defaults apply to
                    // members, so the document keeps the shape an anonymous-object payload produced.
                    var document = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [JsonNamingPolicy.CamelCase.ConvertName(ApiSectionName)] = new { ApiEndpoint = wasmApiEndpoint },
                    };

                    foreach (var (name, value) in builder.Sections)
                    {
                        document[JsonNamingPolicy.CamelCase.ConvertName(name)] = value;
                    }

                    return Results.Ok(document);
                })
                .AllowAnonymous()
                .ExcludeFromDescription();
    }
}
