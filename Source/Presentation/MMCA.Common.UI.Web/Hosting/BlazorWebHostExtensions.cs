using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.API.Startup;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Logging;
using MMCA.Common.Aspire.Security;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Extensions;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Web.ClientConfig;
using MMCA.Common.UI.Web.Hardening;
using MMCA.Common.UI.Web.Pwa;
using MMCA.Common.UI.Web.SameOriginProxy;
using MudBlazor.Services;

namespace MMCA.Common.UI.Web.Hosting;

/// <summary>
/// The composed Blazor web host: the shared registration list, the shared request pipeline and the
/// shared additional-assembly list every Blazor Server + WebAssembly head repeats, as three calls.
/// What differs between heads arrives through <see cref="MmcaBlazorWebHostOptions"/> and
/// <see cref="MmcaBlazorWebPipelineOptions"/>. Render-mode registration stays per head
/// (<c>ConfigureRazorComponents</c>, <c>DevelopmentPipeline</c> and the head's own
/// <c>MapRazorComponents</c> chain), so this package takes no WebAssembly server dependency.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class BlazorWebHostExtensions
{
    private const string ApiEndpointKey = "Api:ApiEndpoint";

    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Registers the shared Blazor web host, in this order: the configuration-source callback, the
        /// Serilog bootstrap, the service defaults, the after-service-defaults callback, Razor components
        /// with interactive server components (then the Razor components callback), bounded circuits and
        /// the UI rate limiter, MudBlazor, the shared UI services and browser capabilities, the session
        /// cookie pair, the trusted-caller header, session-cookie authentication, the authorization
        /// policies, server token storage with the same-origin proxy refresher and then the same-origin
        /// API proxy, the authentication state provider, cascading authentication state, the distributed
        /// memory cache, the web form factor, the Blazor CSP and finally the security headers.
        /// <para>
        /// The head registers its UI modules, <c>IHomePageContent</c> and any <c>IOAuthUISettings</c> of
        /// its own after this call (the shared default is a TryAdd, so a later registration wins). It must
        /// not register <c>ITokenRefresher</c> or <c>ISessionCookieSync</c> after it: the same-origin
        /// proxy replaces both and <c>MapCommonSameOriginApiProxy</c> fails the boot when displaced.
        /// </para>
        /// </summary>
        /// <param name="options">The head-specific values and callbacks.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="InvalidOperationException"><c>Api:ApiEndpoint</c> is not configured.</exception>
        public WebApplicationBuilder AddMmcaBlazorWebHost(MmcaBlazorWebHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(options);

            // Configuration sources first: ConfigurationManager loads each source as it is added, so a
            // vault value is visible to everything below, the service defaults included.
            options.ConfigureConfigurationSources?.Invoke(builder);

            builder.AddCommonSerilog(options.LogFilePath);
            builder.AddServiceDefaults();
            options.AfterServiceDefaults?.Invoke(builder);

            var configuration = builder.Configuration;
            var services = builder.Services;

            var razorComponents = services.AddRazorComponents()
                .AddInteractiveServerComponents(BlazorCircuitLimitExtensions.RetentionFrom(configuration));
            options.ConfigureRazorComponents?.Invoke(razorComponents);

            services.AddBoundedBlazorCircuits();
            services.AddUiRateLimiting(configuration);

            if (options.ConfigureMudServices is null)
            {
                services.AddMudServices();
            }
            else
            {
                services.AddMudServices(options.ConfigureMudServices);
            }

            services.AddUIShared(configuration);
            services.AddBrowserDeviceCapabilities();

            var apiEndpoint = configuration[ApiEndpointKey]
                ?? throw new InvalidOperationException("Api:ApiEndpoint configuration is required for the cookie session refresher.");
            services.AddServerAuthSessionCookie(apiEndpoint);
            services.AddClientAuthSessionCookieSync();

            services.AddTrustedCallerHeader(configuration);

            services.AddAuthentication(SessionCookieAuthenticationHandler.SchemeName)
                .AddSessionCookieAuthentication();
            services.AddAuthorizationPolicies(options.ConfigureFallbackAuthorization);

            // The proxy replaces the circuit's ITokenRefresher and ISessionCookieSync, so it is
            // registered after both (MapCommonSameOriginApiProxy fails the boot otherwise).
            services.AddCommonServerTokenStorage();
            services.AddScoped<ITokenRefresher, SameOriginProxyTokenRefresher>();
            services.AddCommonSameOriginApiProxy(configuration);

            services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
            services.AddCascadingAuthenticationState();
            services.AddDistributedMemoryCache();
            services.AddCommonWebFormFactor();

            // The dynamic Blazor CSP before the security headers, whose static default is a TryAdd.
            services.AddCommonBlazorCsp();
            services.AddCommonSecurityHeaders(configuration);

            return builder;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Builds the shared Blazor web request pipeline, in this order: forwarded headers, security
        /// headers, the UI rate limiter (ahead of the exception handler and the status-code re-execute,
        /// so a throttled request answers 429 without re-rendering a page), the Development callback or
        /// the <c>/Error</c> exception handler, the <c>/not-found</c> re-execute, the health endpoints,
        /// HTTPS redirection, request localization, antiforgery, the authenticated no-store header, the
        /// cookie session refresh, authentication and authorization; then the client-config, session
        /// cookie, same-origin proxy, culture, static-asset and service-worker endpoints.
        /// <para>
        /// The head maps its app-association documents and its own <c>MapRazorComponents</c> chain after
        /// this call, passing <see cref="GetMmcaRazorComponentAssemblies"/> as the additional assemblies.
        /// </para>
        /// </summary>
        /// <param name="options">The head-specific pipeline pieces, or <see langword="null"/> for none.</param>
        /// <returns>The same application, for chaining.</returns>
        public WebApplication UseMmcaBlazorWebPipeline(MmcaBlazorWebPipelineOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(app);

            app.UseCommonUiForwardedHeaders();
            app.UseCommonSecurityHeaders();
            app.UseUiRateLimiting();

            if (app.Environment.IsDevelopment())
            {
                options?.DevelopmentPipeline?.Invoke(app);
            }
            else
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.MapDefaultEndpoints();
            app.UseHttpsRedirection();
            app.UseCommonRequestLocalization();
            app.UseAntiforgery();
            app.UseAuthenticatedNoStore();
            app.UseCookieSessionRefresh();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapClientConfigEndpoint(options?.ClientConfig);
            app.MapSessionCookieEndpoints();
            app.MapCommonSameOriginApiProxy();
            app.MapCultureEndpoint();
            app.MapStaticAssets().AllowAnonymous();
            app.MapCommonServiceWorker();

            return app;
        }

        /// <summary>
        /// The additional assemblies a head passes to <c>MapRazorComponents</c>: MMCA.Common.UI,
        /// MMCA.Common.UI.Web (the shared SSR error page), the head's client assembly, then the assembly
        /// of every registered <see cref="IUIModule"/>, duplicates removed with the first occurrence kept.
        /// </summary>
        /// <param name="clientAssembly">The head's WebAssembly client assembly.</param>
        /// <returns>The ordered, de-duplicated assembly list.</returns>
        public Assembly[] GetMmcaRazorComponentAssemblies(Assembly clientAssembly)
        {
            ArgumentNullException.ThrowIfNull(app);
            ArgumentNullException.ThrowIfNull(clientAssembly);

            Assembly[] framework =
            [
                typeof(MMCA.Common.UI._Imports).Assembly,
                typeof(_Imports).Assembly,
                clientAssembly,
            ];

            return
            [
                .. framework
                    .Concat(app.Services.GetServices<IUIModule>().Select(static module => module.Assembly))
                    .Distinct(),
            ];
        }
    }
}
