using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Globalization;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Navigation;

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
                var apiSettings = serviceProvider.GetRequiredService<IOptions<ApiSettings>>().Value;
                if (string.IsNullOrWhiteSpace(apiSettings.ApiEndpoint))
                {
                    throw new InvalidOperationException("ApiEndpoint is required and cannot be null or empty.");
                }

                client.BaseAddress = new Uri(apiSettings.ApiEndpoint, UriKind.Absolute);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
                .AddHttpMessageHandler<AuthDelegatingHandler>()
                .AddHttpMessageHandler<CultureDelegatingHandler>();

            // TryAdd prevents duplicate registration when called from multiple hosts
            services.TryAddScoped<IAuthUIService, AuthUIService>();
            services.TryAddScoped<ListPageStateService>();
            services.TryAddScoped<ListPageQueryStateService>();
            services.TryAddScoped<NavigationHistoryService>();

            // Day/Dark theme preference (ADR-028): cookie + localStorage persistence, system-pref default.
            services.TryAddScoped<ThemeService>();

            // Per-user culture/theme persistence to the backend (ADR-027/028) — best-effort, anon no-op.
            services.TryAddScoped<IUserPreferenceWriter, ApiUserPreferenceWriter>();
            services.TryAddScoped<IUserPreferenceReader, ApiUserPreferenceReader>();

            // Default no-op OAuth settings — downstream apps override with TryAdd before this runs,
            // or replace after by calling AddSingleton<IOAuthUISettings, ConcreteSettings>()
            services.TryAddSingleton<IOAuthUISettings, DefaultOAuthUISettings>();

            // Device-capability defaults (ADR-042): every contract resolves on every head;
            // MAUI/browser hosts override AFTER this call (last registration wins).
            services.AddDeviceCapabilityDefaults();

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
    }
}

/// <summary>Marker class used to reference the UI.Shared assembly (e.g., for Scrutor scanning).</summary>
public class UISharedAssemblyReference;
