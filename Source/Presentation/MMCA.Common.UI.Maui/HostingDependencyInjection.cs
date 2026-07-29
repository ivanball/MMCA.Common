using MMCA.Common.UI.Maui.Globalization;
using MMCA.Common.UI.Services;
using Plugin.LocalNotification;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// <see cref="MauiAppBuilder"/>-level entry point for the device-capability layer (ADR-042) and the
/// hybrid culture wiring (ADR-027). Call AFTER <c>AddUIShared</c> in <c>MauiProgram.CreateMauiApp</c>.
/// </summary>
public static class HostingDependencyInjection
{
    extension(MauiAppBuilder builder)
    {
        /// <summary>
        /// Registers the native capability implementations plus the platform hooks that need
        /// the builder: Plugin.LocalNotification lifecycle wiring and the notification-tap
        /// deep-link bridge (<see cref="DeviceCapabilitiesInitializer"/>).
        /// <para>
        /// Heads must ALSO chain <c>.UseMauiCommunityToolkit()</c> onto their own
        /// <c>UseMauiApp&lt;T&gt;()</c> call (speech-to-text depends on it): the toolkit's
        /// MCT001 analyzer requires the call to appear in the app's builder chain, so this
        /// wrapper cannot make it for you.
        /// </para>
        /// </summary>
        public MauiAppBuilder UseMauiDeviceCapabilities()
        {
            builder.UseLocalNotification();
            builder.Services.AddMauiDeviceCapabilities();
            builder.Services.AddSingleton<IMauiInitializeService, DeviceCapabilitiesInitializer>();

            // Folded in deliberately, despite belonging to ADR-027 rather than ADR-042: a hybrid head
            // that skips it gets a culture switcher that navigates to a server endpoint it does not
            // host, which renders the not-found page. Every head already makes this call, so wiring it
            // here means no head can be left half-configured. UseMauiCulture() stays separately
            // callable for a head that composes its registrations by hand.
            builder.UseMauiCulture();
            return builder;
        }

        /// <summary>
        /// Wires culture switching for a hybrid head (ADR-027): replaces the web
        /// <c>ICultureApplier</c> (which round-trips the server <c>/culture/set</c> endpoint that no
        /// hybrid head hosts) with the in-process <see cref="MauiCultureApplier"/>, and restores the
        /// persisted culture at startup via <see cref="MauiCultureInitializer"/>.
        /// <para>
        /// Already called by <see cref="UseMauiDeviceCapabilities"/>; calling it twice is harmless.
        /// Must run AFTER <c>AddUIShared</c> so the plain Add overrides that TryAdd default
        /// (last registration wins).
        /// </para>
        /// </summary>
        public MauiAppBuilder UseMauiCulture()
        {
            builder.Services.AddScoped<ICultureApplier, MauiCultureApplier>();
            builder.Services.AddSingleton<IMauiInitializeService, MauiCultureInitializer>();
            return builder;
        }
    }
}
