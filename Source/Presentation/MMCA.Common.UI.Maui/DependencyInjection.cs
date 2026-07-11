using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Maui.Capabilities;
using MMCA.Common.UI.Services.Capabilities;

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
        /// backs natively. Contracts without a native implementation yet (biometrics,
        /// speech-to-text, external-auth broker) keep their null defaults until their
        /// feature wave lands.
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

            // Scoped: navigates through the circuit's NavigationManager after the system-browser
            // round trip. Inert (IsAvailable == false) until the head configures
            // OAuth:MobileRedirectScheme and registers the platform callback.
            services.AddScoped<IExternalAuthBroker, MauiExternalAuthBroker>();
            return services;
        }
    }
}
