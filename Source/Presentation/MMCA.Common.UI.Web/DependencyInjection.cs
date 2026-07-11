using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
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
    }
}
