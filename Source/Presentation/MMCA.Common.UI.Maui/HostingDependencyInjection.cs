using Microsoft.Extensions.DependencyInjection;
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
