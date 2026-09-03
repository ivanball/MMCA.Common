using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Resilience;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Globalization;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Caching;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Culture;
using MMCA.Common.UI.Services.Navigation;
using MMCA.Common.UI.Services.Preferences;
using MMCA.Common.UI.Theme;

namespace MMCA.Common.UI;

/// <summary>
/// Registers services shared across all UI hosts (Blazor Server, WebAssembly, MAUI).
/// Uses C# preview extension types to add <c>AddUIShared</c> directly to <see cref="IServiceCollection"/>.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers shared UI infrastructure: API settings, named <c>"APIClient"</c> HttpClient with
        /// JWT auth handler, authentication service, and cart state service.
        /// </summary>
        public IServiceCollection AddUIShared(IConfiguration configuration)
        {
            // Bind and validate API settings at startup to fail fast on missing configuration
            services.AddOptions<ApiSettings>()
                .Bind(configuration.GetSection(ApiSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Bind layout settings (footer text, etc.) — optional, defaults to empty values
            services.AddOptions<LayoutSettings>()
                .Bind(configuration.GetSection(LayoutSettings.SectionName));

            // Client-side staleness policy (§19). Both sections are optional: an absent section leaves
            // the compiled-in defaults, which is the behaviour a host gets without configuring anything.
            services.AddOptions<UiReadCacheOptions>()
                .Bind(configuration.GetSection(UiReadCacheOptions.SectionName));

            services.AddOptions<NotificationBellOptions>()
                .Bind(configuration.GetSection(NotificationBellOptions.SectionName));

            // The clock the staleness policy is measured against. TryAdd, because a host that already
            // registered one (AddInfrastructure does) keeps it, and a test substitutes a FakeTimeProvider.
            services.TryAddSingleton(TimeProvider.System);

            // Read-through cache over the API client, scoped so it is per-circuit on Blazor Server.
            // On WebAssembly and MAUI the scope is the app lifetime, which is why the sign-out path
            // clears it (AuthUIService.LogoutAsync): otherwise one account's reads outlive its session.
            services.TryAddScoped<IUiReadCache, UiReadCache>();

            // Resource-based localization for IStringLocalizer<T> across all UI hosts (ADR-027).
            services.AddLocalization();

            // Pseudo-localization decorator (ADR-027 §8): wraps the localizer factory so every resolved
            // string is runtime-transformed (accents + padding + bracket sentinel) when the current UI
            // culture is the pseudo locale. Registered unconditionally because it is inert under every
            // other culture; the pseudo locale is only ever activatable in Development (request
            // localization + the culture switcher add it there only).
            services.Decorate<IStringLocalizerFactory, PseudoStringLocalizerFactory>();

            // MudBlazor built-in component text (pager, filter menus, pickers, close buttons) follows
            // the active culture via the MudTranslations resource pair (ADR-027). AddMudServices does
            // not register a MudLocalizer of its own (guarded by a DI resolution test), so TryAdd is
            // authoritative regardless of host registration order.
            services.TryAddTransient<MudBlazor.MudLocalizer, ResxMudLocalizer>();

            // Auth handler injects Bearer token into every outgoing API request; culture handler forwards
            // the active UI culture as Accept-Language so the API localizes error messages to match.
            services.AddTransient<AuthDelegatingHandler>();
            services.AddTransient<CultureDelegatingHandler>();

            // Named HttpClient used by all EntityServiceBase-derived services
            services.AddHttpClient("APIClient", (serviceProvider, client) =>
            {
                // No endpoint guard here: resolving IOptions<ApiSettings>.Value runs the
                // ValidateDataAnnotations rules registered above, so a missing [Required] ApiEndpoint
                // already fails as an OptionsValidationException (at startup via ValidateOnStart, and
                // again here for any host that skips the startup validator). A second hand-written
                // check would only give the same failure a different, less informative exception.
                var apiSettings = serviceProvider.GetRequiredService<IOptions<ApiSettings>>().Value;

                // Null-forgiving: the [Required] annotation above is what guarantees this is populated.
                client.BaseAddress = new Uri(apiSettings.ApiEndpoint!, UriKind.Absolute);

                // HttpClient's own default is 100s, chosen by the BCL with no knowledge of the
                // resilience budget: it would cut a call off mid-policy at an arbitrary point.
                // Pinning it to the shared total-request timeout (90s) keeps the two coordinated,
                // so the budget decides when to give up and the transport never pre-empts it.
                client.Timeout = HttpResilienceDefaults.TotalRequestTimeout;
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
                .AddHttpMessageHandler<AuthDelegatingHandler>()
                .AddHttpMessageHandler<CultureDelegatingHandler>();

            // Toast and confirm-dialog facades, factored out so a bUnit harness can register exactly
            // these two without pulling in the whole shared-UI surface.
            services.AddCommonUiFacades();

            // TryAdd prevents duplicate registration when called from multiple hosts
            services.TryAddScoped<IAuthUIService, AuthUIService>();
            services.TryAddScoped<ListPageStateService>();
            services.TryAddScoped<ListPageQueryStateService>();
            services.TryAddScoped<NavigationHistoryService>();

            // Day/Dark theme preference (ADR-028): cookie + localStorage persistence, system-pref default.
            services.TryAddScoped<ThemeService>();

            // Culture switching (ADR-027). The default round-trips the server /culture/set endpoint, which
            // only exists on a Blazor Web head; MAUI Blazor Hybrid heads override this AFTER AddUIShared
            // with an in-process applier (UseMauiDeviceCapabilities does it), since a hybrid head has no
            // ASP.NET pipeline and the endpoint URL would resolve to the Blazor not-found page.
            services.TryAddScoped<ICultureApplier, EndpointCultureApplier>();

            // Shareable public links (share sheet, copy-link, QR). The default resolves against the
            // browser origin, which is correct for the Server and WebAssembly heads; a MAUI Blazor
            // Hybrid head overrides it AFTER AddUIShared with AddCommonMauiPublicLinkBuilder(),
            // because its WebView origin is a virtual host nobody else can open.
            services.TryAddScoped<IPublicLinkBuilder, NavigationPublicLinkBuilder>();

            // Per-user culture/theme persistence to the backend (ADR-027/028) — best-effort, anon no-op.
            services.TryAddScoped<IUserPreferenceWriter, ApiUserPreferenceWriter>();
            services.TryAddScoped<IUserPreferenceReader, ApiUserPreferenceReader>();

            // Default no-op OAuth settings — downstream apps override with TryAdd before this runs,
            // or replace after by calling AddSingleton<IOAuthUISettings, ConcreteSettings>()
            services.TryAddSingleton<IOAuthUISettings, DefaultOAuthUISettings>();

            // Device-capability defaults (ADR-042): every contract resolves on every head.
            // MAUI/browser hosts override AFTER this call (last registration wins).
            services.AddDeviceCapabilityDefaults();

            return services;
        }

        /// <summary>
        /// Registers the vendor-neutral toast and confirm-dialog facades over MudBlazor:
        /// <c>IToastService</c> and <c>IAppDialogService</c>. These two implementations are the ONLY
        /// types in the framework that name MudBlazor's <c>ISnackbar</c> / <c>IDialogService</c>:
        /// every page, component and <c>Result</c> helper depends on the contracts instead, so the
        /// component library stays swappable and a test can record toasts without a rendered
        /// snackbar host. Scoped, to match the MudBlazor services they wrap.
        /// <para>
        /// Called by <c>AddUIShared</c>, and separately by the shipped bUnit base
        /// (<c>MMCA.Common.Testing.UI</c>) so a component test resolves the facades without the rest
        /// of the shared-UI surface. Registered with TryAdd, so a host or test that pre-registers a
        /// substitute keeps it.
        /// </para>
        /// </summary>
        public IServiceCollection AddCommonUiFacades()
        {
            services.TryAddScoped<IToastService, MudToastService>();
            services.TryAddScoped<IAppDialogService, MudAppDialogService>();
            return services;
        }

        /// <summary>
        /// Registers the session-cookie sync used to mirror the client's in-memory tokens into the
        /// HttpOnly cookie read by server-side SSR prerender. Called from both the Blazor
        /// Server (UI.Web) host and the WebAssembly client.
        /// </summary>
        public IServiceCollection AddClientAuthSessionCookieSync()
        {
            services.TryAddScoped<ISessionCookieSync, JsFetchSessionCookieSync>();
            return services;
        }

        /// <summary>
        /// Registers the WebAssembly <see cref="IFormFactor"/> (<see cref="WasmFormFactor"/>: reports
        /// "WebAssembly" plus the browser-reported OS description). Call from the WASM .Client host;
        /// the Blazor Server head registers <c>AddCommonWebFormFactor()</c> (MMCA.Common.UI.Web) and
        /// the MAUI head <c>AddMauiFormFactor()</c> (MMCA.Common.UI.Maui) instead.
        /// </summary>
        public IServiceCollection AddWasmFormFactor() =>
            services.AddSingleton<IFormFactor, WasmFormFactor>();

        /// <summary>
        /// Registers one UI module: the Scrutor scan that picks up every
        /// <see cref="IEntityService{TEntityDTO, TIdentifierType}"/> implementation in
        /// <typeparamref name="TModule"/>'s assembly, plus the module descriptor itself (nav items,
        /// app-bar and layout components, and the assembly the router adds to
        /// <c>AdditionalAssemblies</c>).
        /// <para>
        /// This is the two-step prologue every module's own <c>Add{Module}UI()</c> opens with;
        /// calling it removes the copy of the scan from each of them. Module-specific services
        /// (lookup services, state containers, custom contracts) are registered by the caller
        /// afterwards, so a module whose services must win over a shared default still controls
        /// its own registration order.
        /// </para>
        /// </summary>
        /// <typeparam name="TModule">
        /// The module descriptor type. Its assembly is the scan root, so it must live alongside the
        /// module's entity services and Razor pages.
        /// </typeparam>
        public IServiceCollection AddUIModule<TModule>()
            where TModule : class, IUIModule
        {
            services.Scan(scan => scan
                .FromAssemblyOf<TModule>()
                .AddClasses(classes => classes.AssignableTo(typeof(IEntityService<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            return services.AddSingleton<IUIModule, TModule>();
        }
    }
}

/// <summary>Marker class used to reference the UI.Shared assembly (e.g., for Scrutor scanning).</summary>
public class UISharedAssemblyReference;
