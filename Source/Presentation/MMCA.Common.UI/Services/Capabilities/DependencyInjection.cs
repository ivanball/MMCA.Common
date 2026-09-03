using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.UI.Services.Capabilities.Accessibility;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.Services.Capabilities.Location;
using MMCA.Common.UI.Services.Capabilities.Media;
using MMCA.Common.UI.Services.Capabilities.Navigation;
using MMCA.Common.UI.Services.Capabilities.Notifications;

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
        /// <para>
        /// Public so a consumer's bUnit test base can register the same set the production host gets
        /// instead of mirroring it by hand: a hand-mirrored list silently rots the moment a new
        /// capability contract ships here, and the component test that needed it fails with a DI
        /// resolution error rather than a useful one.
        /// </para>
        /// </summary>
        public IServiceCollection AddDeviceCapabilityDefaults()
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

            // Native push registration (ADR-044): both default to inert. UI.Maui overrides the
            // registration service; the app overrides the token provider once real FCM/APNs
            // credentials exist - until then even native heads stay registered-but-tokenless.
            services.TryAddSingleton<IPushRegistrationService, NullPushRegistrationService>();
            services.TryAddSingleton<IPushDeviceTokenProvider, NullPushDeviceTokenProvider>();

            // Media picking (ADR-045): web heads render InputFile instead (IsSupported false).
            services.TryAddSingleton<IMediaPickerService, NullMediaPickerService>();

            // Camera barcode/QR scanning (ADR-042): no browser primitive, so web heads hide the
            // affordance. The native override is opt-in (UseCommonBarcodeScanner in UI.Maui), so
            // even a MAUI head keeps this default until it asks for the camera.
            services.TryAddSingleton<IBarcodeScannerService, NullBarcodeScannerService>();

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
