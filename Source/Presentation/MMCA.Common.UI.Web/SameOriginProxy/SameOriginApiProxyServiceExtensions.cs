using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// Registration half of the opt-in same-origin API proxy (backend-for-frontend) for a Blazor Web host:
/// the browser calls <c>/api/**</c> on the UI host's own origin and the host forwards to the gateway
/// with the bearer taken from the HttpOnly session cookie, so neither the access token nor the refresh
/// token is ever readable by script on the page. A host that does not call it is unchanged. The
/// endpoint half is <see cref="SameOriginApiProxyEndpointExtensions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changes when a host opts in.</b> The session cookies become <c>SameSite=Strict</c> (by
/// default) and claims-only toward the browser: <c>/auth/session/token</c> answers with
/// <see cref="SessionClaimsToken"/> and <c>POST /auth/session-cookie</c> stops accepting tokens from
/// script. <c>/client-config</c> adds <c>api.sameOriginApiEndpoint</c>, which switches the WebAssembly
/// client's <c>"APIClient"</c> and notification hub to the proxy. The Blazor Server circuit keeps
/// calling the gateway server-to-server and exchanges tokens with the cookies only as protected
/// handoffs (<c>/auth/session/handoff</c>, <c>/auth/session-cookie/handoff</c>).
/// </para>
/// <para>
/// <b>Ordering.</b> <c>AddServerAuthSessionCookie</c> (the cookie refresher this proxy reuses),
/// <c>AddCommonServerTokenStorage</c>, and the host's own <c>ITokenRefresher</c> /
/// <c>AddClientAuthSessionCookieSync</c> registrations come BEFORE this call, because it replaces the
/// Server circuit's <see cref="ITokenRefresher"/> and <see cref="ISessionCookieSync"/>;
/// <c>MapCommonSameOriginApiProxy</c> fails the boot when a later registration displaced them.
/// </para>
/// </remarks>
public static class SameOriginApiProxyServiceExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the same-origin API proxy: binds and validates <see cref="SameOriginApiProxySettings"/>
        /// (section <c>"SameOriginApiProxy"</c>, gateway defaulting to <c>Api:ApiEndpoint</c>), tightens the
        /// session cookies (Strict, claims-only toward the browser), registers YARP's forwarder, and
        /// replaces the Blazor Server circuit's <see cref="ITokenRefresher"/> and
        /// <see cref="ISessionCookieSync"/> with the protected-handoff implementations. Pair with
        /// <c>MapCommonSameOriginApiProxy()</c>.
        /// </summary>
        /// <param name="configuration">The host's configuration.</param>
        /// <returns>The service collection, for chaining.</returns>
        public IServiceCollection AddCommonSameOriginApiProxy(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<SameOriginApiProxySettings>()
                .Bind(configuration.GetSection(SameOriginApiProxySettings.SectionName))
                .PostConfigure(settings =>
                {
                    // Reuse how the host already reaches the gateway server-side, discovery name included.
                    if (string.IsNullOrWhiteSpace(settings.GatewayAddress))
                    {
                        settings.GatewayAddress = configuration.GetSection(ApiSettings.SectionName)[nameof(ApiSettings.ApiEndpoint)];
                    }
                })
                .ValidateOnStart();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<SameOriginApiProxySettings>, SameOriginApiProxySettingsValidator>());

            services.AddOptions<SessionCookieSettings>()
                .Configure<IOptions<SameOriginApiProxySettings>>((cookie, proxy) =>
                {
                    cookie.SameSite = proxy.Value.SessionCookieSameSite;
                    cookie.ClaimsOnlyBrowserTokens = true;
                });

            services.AddHttpForwarder();
            services.AddDataProtection();
            services.TryAddSingleton(static sp => new SameOriginProxyInvoker(sp));
            services.TryAddSingleton<SameOriginApiProxyEndpoint>();
            services.TryAddSingleton<SessionHandoffProtector>();
            services.TryAddSingleton<SameOriginApiProxyMarker>();

            services.Replace(ServiceDescriptor.Scoped<ITokenRefresher, HandoffTokenRefresher>());
            services.Replace(ServiceDescriptor.Scoped<ISessionCookieSync, HandoffSessionCookieSync>());

            return services;
        }
    }
}

/// <summary>Registered only by <c>AddCommonSameOriginApiProxy</c>: tells other host pieces the proxy is on.</summary>
internal sealed class SameOriginApiProxyMarker;
