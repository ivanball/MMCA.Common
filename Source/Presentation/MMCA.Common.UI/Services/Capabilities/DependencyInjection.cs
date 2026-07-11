using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.UI.Services.Capabilities.Browser;
using MMCA.Common.UI.Services.Capabilities.Fallbacks;

namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Device-capability registration (ADR-042). <c>AddUIShared</c> TryAdd-registers a safe
/// default for every contract so shared components resolve them on any head; heads then
/// override with plain Add registrations AFTER <c>AddUIShared</c> — last registration wins
/// for single-service resolution. Browser overrides live here
/// (<see cref="AddBrowserDeviceCapabilities"/>); native overrides ship in the
/// <c>MMCA.Common.UI.Maui</c> package (<c>AddMauiDeviceCapabilities</c>).
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the null/neutral default for every capability contract. Called by
        /// <c>AddUIShared</c>; TryAdd keeps repeated host calls idempotent.
        /// </summary>
        internal IServiceCollection AddDeviceCapabilityDefaults()
        {
            // Stateless no-op defaults — singletons.
            services.TryAddSingleton<IConnectivityStatusService, AlwaysOnlineConnectivityStatusService>();
            services.TryAddSingleton<IShareService, NullShareService>();
            services.TryAddSingleton<IClipboardService, NullClipboardService>();
            services.TryAddSingleton<IHapticFeedbackService, NullHapticFeedbackService>();
            services.TryAddSingleton<IMapNavigationService, NullMapNavigationService>();
            services.TryAddSingleton<IGeolocationService, NullGeolocationService>();
            services.TryAddSingleton<IGeocodingService, NullGeocodingService>();
            services.TryAddSingleton<IExternalLinkService, NullExternalLinkService>();
            services.TryAddSingleton<ITextToSpeechService, NullTextToSpeechService>();
            services.TryAddSingleton<IAccessibilityAnnouncer, NullAccessibilityAnnouncer>();
            services.TryAddSingleton<ILocalNotificationService, NullLocalNotificationService>();
            services.TryAddSingleton<IScreenshotService, NullScreenshotService>();
            services.TryAddSingleton<IBatteryStatusService, NullBatteryStatusService>();
            services.TryAddSingleton<IBiometricAuthenticator, NullBiometricAuthenticator>();
            services.TryAddSingleton<ISpeechToTextService, NullSpeechToTextService>();
            services.TryAddSingleton<IExternalAuthBroker, UnavailableExternalAuthBroker>();
            services.TryAddSingleton<ILocalCacheStore, NullLocalCacheStore>();

            // Scoped so the Blazor Server fallback holds per-circuit (per-user) state,
            // never cross-user state.
            services.TryAddScoped<IDevicePreferences, InMemoryDevicePreferences>();

            // Singleton by contract: native code publishes into it from outside any scope.
            // Web heads have no native publishers, so the shared buffer is inert there.
            services.TryAddSingleton<IDeepLinkDispatcher, DeepLinkDispatcher>();

            return services;
        }

        /// <summary>
        /// Overrides the capability defaults with the browser implementations
        /// (<c>navigator.share</c>, clipboard, <c>aria-live</c> announcements,
        /// online/offline watching, <c>localStorage</c> preferences and cache). Call AFTER
        /// <c>AddUIShared</c> from the Blazor Server and WebAssembly hosts. Every
        /// implementation is prerender-safe: JS-unavailable calls degrade to the null
        /// behavior instead of throwing.
        /// </summary>
        public IServiceCollection AddBrowserDeviceCapabilities()
        {
            // One JS module import per scope/circuit, shared by all browser services.
            services.AddScoped<CapabilitiesJsModule>();

            services.AddScoped<IShareService, BrowserShareService>();
            services.AddScoped<IClipboardService, BrowserClipboardService>();
            services.AddScoped<IExternalLinkService, BrowserExternalLinkService>();
            services.AddScoped<IAccessibilityAnnouncer, BrowserAccessibilityAnnouncer>();
            services.AddScoped<IConnectivityStatusService, BrowserConnectivityStatusService>();
            services.AddScoped<IDevicePreferences, BrowserDevicePreferences>();
            services.AddScoped<ILocalCacheStore, BrowserLocalCacheStore>();
            services.AddScoped<IMapNavigationService, BrowserMapNavigationService>();

            return services;
        }
    }
}
