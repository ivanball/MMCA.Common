using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using MMCA.Common.Aspire.Gateway;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Web.Security;
using MMCA.Common.UI.Web.Services;

namespace MMCA.Common.UI.Web;

/// <summary>
/// Registration extensions for the server-side Blazor Web host pieces this package ships. Hosts call
/// these from <c>Program.cs</c> instead of registering app-local copies of the implementations.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the cookie-backed Blazor Server token storage
        /// (<see cref="ServerTokenStorageService"/> as the scoped <c>ITokenStorageService</c>): the
        /// HttpOnly session cookie during SSR prerender, an in-memory token hydrated via the
        /// same-origin refresh endpoint on the interactive circuit (ADR-022). Pair with the session
        /// cookie plumbing from MMCA.Common.API (<c>AddServerAuthSessionCookie</c> /
        /// <c>UseCookieSessionRefresh</c>) and a registered <c>ITokenRefresher</c>.
        /// </summary>
        public IServiceCollection AddCommonServerTokenStorage()
        {
            services.AddHttpContextAccessor();
            return services.AddScoped<ITokenStorageService, ServerTokenStorageService>();
        }

        /// <summary>
        /// Registers the Blazor host's dynamic Content-Security-Policy provider
        /// (<c>connect-src</c> pinned to the configured API/Gateway origin from <c>ApiSettings</c>,
        /// permissive Report-Only fallback on misconfiguration). Call BEFORE
        /// <c>AddCommonSecurityHeaders</c> so it wins over the default static provider (which is
        /// registered with <c>TryAdd</c>).
        /// </summary>
        public IServiceCollection AddCommonBlazorCsp() =>
            services.AddSingleton<ICspPolicyProvider, BlazorCspPolicyProvider>();

        /// <summary>
        /// Registers the Blazor Server <see cref="IFormFactor"/> (<see cref="WebFormFactor"/>: reports
        /// "Web" plus the server OS description). The WASM client registers <c>AddWasmFormFactor()</c>
        /// from MMCA.Common.UI instead.
        /// </summary>
        public IServiceCollection AddCommonWebFormFactor() =>
            services.AddSingleton<IFormFactor, WebFormFactor>();

        /// <summary>
        /// Presents the deployment's trusted-internal-caller secret on this host's server-to-server
        /// calls to the gateway, so they take the gateway's no-limiter partition instead of
        /// collapsing every visitor into one client-IP window. Composes
        /// <see cref="TrustedCallerHandler"/> onto every <c>HttpClient</c> the host creates; the
        /// handler stamps only requests whose origin is the gateway.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Opt in.</b> The whole feature is off unless
        /// <c>GatewayRateLimiting:TrustedCallerSecret</c> is set: with no secret (the local and CI
        /// default) this registers nothing at all and every call stays rate limited exactly as it is
        /// today. The gateway must be configured from the SAME
        /// <c>GatewayRateLimiting</c> section, so one section configures both ends
        /// (<see cref="GatewayRateLimitingSettings"/>).
        /// </para>
        /// <para>
        /// <b>Server only.</b> The secret exempts a component this deployment ships, never a
        /// visitor's browser, so it must never reach client-side code: supply it from a secret store
        /// or the environment (<c>GatewayRateLimiting__TrustedCallerSecret</c>) to the SSR host
        /// alone, never to the WebAssembly client or a rendered page.
        /// </para>
        /// <para>
        /// <b>Why every client.</b> The call that suffers most from the per-IP collapse is the
        /// cookie-session token refresh, whose client the framework creates under a name a host has
        /// no supported way to reach, so there is no single name to configure. The gateway origin
        /// comes from <c>Api:ApiEndpoint</c> (<see cref="ApiSettings"/>), the endpoint this host's
        /// server-side calls already target; a host whose endpoint is missing or not an absolute URI
        /// registers nothing.
        /// </para>
        /// </remarks>
        /// <param name="configuration">The host's configuration.</param>
        /// <returns>The service collection, for chaining.</returns>
        public IServiceCollection AddTrustedCallerHeader(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var settings = configuration.GetSection(GatewayRateLimitingSettings.SectionName)
                .Get<GatewayRateLimitingSettings>() ?? new GatewayRateLimitingSettings();

            // ApiEndpoint, not WasmApiEndpoint: this stamps SERVER-side calls, and the browser-facing
            // endpoint the CSP pins is a different question.
            var apiEndpoint = configuration.GetSection(ApiSettings.SectionName)
                .Get<ApiSettings>()?.ApiEndpoint;

            if (string.IsNullOrWhiteSpace(settings.TrustedCallerSecret)
                || string.IsNullOrWhiteSpace(settings.TrustedCallerHeaderName)
                || !Uri.TryCreate(apiEndpoint, UriKind.Absolute, out var gatewayOrigin))
            {
                return services;
            }

            var headerName = settings.TrustedCallerHeaderName;
            var secret = settings.TrustedCallerSecret;

            // Insert(0), not Add: index 0 is the OUTERMOST handler, so the origin gate judges the
            // authority the caller wrote, which is the authority this host configured. Appending
            // would put the gate inside whatever AddServiceDefaults registered, and Aspire's
            // service-discovery handler REWRITES the authority mid-pipeline: a host whose
            // Api:ApiEndpoint is a discovery name (https+http://gateway) would present the resolved
            // authority (https://gateway.local) to a gate configured with the unresolved one, never
            // match, and silently lose the rate-limit exemption. Inserting at the front also makes
            // the answer independent of whether a host calls AddServiceDefaults before or after
            // this, which is not something a host should have to know.
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(builder =>
                    builder.AdditionalHandlers.Insert(
                        0,
                        new TrustedCallerHandler(headerName, secret, gatewayOrigin))));

            return services;
        }
    }
}
