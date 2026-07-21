using Plugin.LocalNotification;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// <see cref="MauiAppBuilder"/>-level entry point for the device-capability layer (ADR-042).
/// Call AFTER <c>AddUIShared</c> in <c>MauiProgram.CreateMauiApp</c>.
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
            return builder;
        }
    }
}
