using MMCA.Common.UI.Maui.Capabilities;
using MMCA.Common.UI.Maui.Capabilities.Accessibility;
using MMCA.Common.UI.Maui.Capabilities.Auth;
using MMCA.Common.UI.Maui.Capabilities.DeviceStatus;
using MMCA.Common.UI.Maui.Capabilities.DeviceStorage;
using MMCA.Common.UI.Maui.Capabilities.Interop;
using MMCA.Common.UI.Maui.Capabilities.Location;
using MMCA.Common.UI.Maui.Capabilities.Media;
using MMCA.Common.UI.Maui.Capabilities.Notifications;
using MMCA.Common.UI.Maui.Services;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Accessibility;
using MMCA.Common.UI.Services.Capabilities.Auth;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Capabilities.Interop;
using MMCA.Common.UI.Services.Capabilities.Location;
using MMCA.Common.UI.Services.Capabilities.Media;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// Native device-capability registration for MAUI Blazor Hybrid heads (ADR-042). Prefer
/// <c>builder.UseMauiDeviceCapabilities()</c> (see <see cref="HostingDependencyInjection"/>),
/// which also wires the Plugin.LocalNotification lifecycle hooks; call this service-level
/// registration AFTER <c>AddUIShared</c> so these plain Add registrations override the
/// TryAdd defaults (last registration wins).
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the MAUI implementations for every capability the framework currently
        /// backs natively, including biometrics, speech-to-text, and the external-auth
        /// broker (the broker stays inert until the head configures
        /// OAuth:MobileRedirectScheme; see the registration below).
        /// </summary>
        public IServiceCollection AddMauiDeviceCapabilities()
        {
            // Singletons throughout: a MAUI head is single-user, and the stateful services
            // (connectivity, battery) wrap app-global platform events.
            services.AddSingleton<IConnectivityStatusService, MauiConnectivityStatusService>();
            services.AddSingleton<IBatteryStatusService, MauiBatteryStatusService>();
            services.AddSingleton<IShareService, MauiShareService>();
            services.AddSingleton<IClipboardService, MauiClipboardService>();
            services.AddSingleton<IHapticFeedbackService, MauiHapticFeedbackService>();
            services.AddSingleton<IMapNavigationService, MauiMapNavigationService>();
            services.AddSingleton<IGeolocationService, MauiGeolocationService>();
            services.AddSingleton<IGeocodingService, MauiGeocodingService>();
            services.AddSingleton<IExternalLinkService, MauiExternalLinkService>();
            services.AddSingleton<ITextToSpeechService, MauiTextToSpeechService>();
            services.AddSingleton<IAccessibilityAnnouncer, MauiAccessibilityAnnouncer>();
            services.AddSingleton<ILocalNotificationService, MauiLocalNotificationService>();
            services.AddSingleton<IScreenshotService, MauiScreenshotService>();
            services.AddSingleton<IDevicePreferences, MauiDevicePreferences>();
            services.AddSingleton<ILocalCacheStore, MauiLocalCacheStore>();
            services.AddSingleton<IBiometricAuthenticator, MauiBiometricAuthenticator>();
            services.AddSingleton<ISpeechToTextService, MauiSpeechToTextService>();

            // Native push registration (ADR-044). Real deliveries additionally need the app to
            // register a credentialed IPushDeviceTokenProvider; the shared default yields no
            // token, so this stays wired-but-inert until push credentials exist.
            services.AddSingleton<IPushRegistrationService, MauiPushRegistrationService>();

            // Photo pick/capture for avatar upload (ADR-045). Capture prompts for the camera
            // permission; the head must declare it (Android CAMERA + iOS usage strings).
            services.AddSingleton<IMediaPickerService, MauiMediaPickerService>();

            // Scoped: navigates through the circuit's NavigationManager after the system-browser
            // round trip. Inert (IsAvailable == false) until the head configures
            // OAuth:MobileRedirectScheme and registers the platform callback.
            services.AddScoped<IExternalAuthBroker, MauiExternalAuthBroker>();
            return services;
        }

        /// <summary>
        /// Registers the MAUI token pipeline: <see cref="MauiSecureTokenStore"/> as the scoped
        /// <c>ISecureTokenStore</c> (the platform secure enclave holds both tokens, and every
        /// read/write is guarded so an OS-invalidated keystore entry degrades to one clean re-login
        /// instead of an unhandled throw on launch) and <see cref="MauiTokenStorageService"/> as the
        /// scoped <c>ITokenStorageService</c> on top of it, which checks expiry and refreshes
        /// proactively rather than handing callers a stale bearer.
        /// <para>
        /// Both halves are required. The split is what keeps the graph acyclic: storage depends on
        /// the refresher, and the refresher depends on the raw store rather than back on storage.
        /// </para>
        /// <para>
        /// The browser-host equivalents are <c>AddCommonServerTokenStorage()</c> (MMCA.Common.UI.Web)
        /// and the WASM <c>WasmTokenStorageService</c> (MMCA.Common.UI). Scoped rather than singleton
        /// to match those siblings, so component code can depend on one lifetime across every head.
        /// </para>
        /// </summary>
        public IServiceCollection AddCommonMauiTokenStorage()
        {
            services.AddScoped<ISecureTokenStore, MauiSecureTokenStore>();
            return services.AddScoped<ITokenStorageService, MauiTokenStorageService>();
        }

        /// <summary>
        /// Registers this platform's credentialed <see cref="IPushDeviceTokenProvider"/> (ADR-044):
        /// the FCM registration token on Android, the APNs device token on iOS/MacCatalyst. The
        /// windows TFM registers nothing and keeps the framework's null default, so the pipeline
        /// stays inert there.
        /// <para>
        /// Call it AFTER <c>AddUIShared</c>: that is where the null default is TryAdd-registered,
        /// and a plain Add only beats it by being the last registration. Both providers are
        /// configuration-gated (<c>Push:Fcm</c> credentials / <c>Push:Apns:Enabled</c>), so a head
        /// with no push configuration stays inert even after calling this. The platform wiring the
        /// providers depend on stays app-side: the Android POST_NOTIFICATIONS declaration and
        /// credentials, and on iOS the aps-environment entitlement plus the two AppDelegate
        /// callbacks that publish into <c>ApnsTokenBridge</c>.
        /// </para>
        /// </summary>
        public IServiceCollection AddMauiPushDeviceTokenProvider()
        {
#if ANDROID
            services.AddSingleton<IPushDeviceTokenProvider, FcmPushDeviceTokenProvider>();
#elif IOS || MACCATALYST
            services.AddSingleton<IPushDeviceTokenProvider, ApnsPushDeviceTokenProvider>();
#endif
            return services;
        }

        /// <summary>
        /// Registers <see cref="MauiPublicLinkBuilder"/> as the <c>IPublicLinkBuilder</c>, so share,
        /// copy-link and QR affordances emit the public web URL (<c>PublicSite:BaseUrl</c>) instead
        /// of the WebView's internal origin. Call it AFTER <c>AddUIShared</c> and after any module
        /// registration that registers a builder of its own: last registration wins.
        /// </summary>
        public IServiceCollection AddCommonMauiPublicLinkBuilder() =>
            services.AddScoped<IPublicLinkBuilder, MauiPublicLinkBuilder>();

        /// <summary>
        /// Registers the native <see cref="IFormFactor"/> (<see cref="MauiFormFactor"/>: DeviceInfo
        /// idiom plus platform and version). Deliberately separate from
        /// <see cref="AddMauiDeviceCapabilities"/> so heads that still register their own
        /// implementation keep last-registration-wins control.
        /// </summary>
        public IServiceCollection AddMauiFormFactor() =>
            services.AddSingleton<IFormFactor, MauiFormFactor>();
    }
}
