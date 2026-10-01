using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// Endpoint half of the opt-in same-origin API proxy; the registration half and the full description
/// are on <see cref="SameOriginApiProxyServiceExtensions"/>.
/// </summary>
public static class SameOriginApiProxyEndpointExtensions
{
    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps <c>{PathPrefix}/{**path}</c> (every method, WebSocket upgrades included) to the gateway,
        /// plus the Blazor Server circuit's two handoff endpoints (<c>POST /auth/session/handoff</c>,
        /// <c>POST /auth/session-cookie/handoff</c>). All are anonymous by declaration (the gateway
        /// authorizes the forwarded bearer; the handoffs authenticate through the cookies), carry no
        /// antiforgery requirement (the <c>X-CSRF: 1</c> header is the gate on every unsafe method) and
        /// stay out of the OpenAPI description. Map it after <c>UseAuthorization</c>, alongside the
        /// host's other endpoints.
        /// </summary>
        /// <returns>The endpoint route builder, for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// <c>AddCommonSameOriginApiProxy</c> was not called, or a later registration replaced the Server
        /// circuit's <see cref="ITokenRefresher"/> or <see cref="ISessionCookieSync"/>.
        /// </exception>
        public IEndpointRouteBuilder MapCommonSameOriginApiProxy()
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            var services = endpoints.ServiceProvider;
            if (services.GetService<SameOriginApiProxyMarker>() is null)
            {
                throw new InvalidOperationException(
                    "MapCommonSameOriginApiProxy requires services.AddCommonSameOriginApiProxy(configuration).");
            }

            VerifyCircuitRegistrations(services);

            var settings = services.GetRequiredService<IOptions<SameOriginApiProxySettings>>().Value;
            var proxy = services.GetRequiredService<SameOriginApiProxyEndpoint>();

            endpoints.Map(settings.PathPrefix + "/{**path}", proxy.InvokeAsync)
                .ExcludeFromDescription()
                .AllowAnonymous()
                .DisableAntiforgery();

            SessionHandoffEndpoints.Map(endpoints);
            return endpoints;
        }
    }

    /// <summary>
    /// A host that registers its own <see cref="ITokenRefresher"/> (or session-cookie sync) AFTER opting
    /// in would silently put the access token back in the page on every Server-circuit hydration; that
    /// is a wiring error, so the boot fails naming it.
    /// </summary>
    private static void VerifyCircuitRegistrations(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        Verify<ITokenRefresher, HandoffTokenRefresher>(scope.ServiceProvider);
        Verify<ISessionCookieSync, HandoffSessionCookieSync>(scope.ServiceProvider);
    }

    private static void Verify<TService, TExpected>(IServiceProvider services)
        where TService : class
    {
        TService? resolved;
        try
        {
            resolved = services.GetService<TService>();
        }
        catch (InvalidOperationException)
        {
            // A dependency (IJSRuntime) is missing: the host has no interactive Server circuit to protect.
            return;
        }

        if (resolved is not null and not TExpected)
        {
            throw new InvalidOperationException(
                $"{typeof(TService).Name} resolves to {resolved.GetType().Name}, which hands tokens to script. " +
                $"Call AddCommonSameOriginApiProxy AFTER every {typeof(TService).Name} registration " +
                "(AddClientAuthSessionCookieSync, AddScoped<ITokenRefresher, ...>) so the protected-handoff implementation wins.");
        }
    }
}
