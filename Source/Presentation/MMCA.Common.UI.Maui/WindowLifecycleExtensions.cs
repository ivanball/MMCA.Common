using MMCA.Common.UI.Services.Capabilities.DeviceStatus;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// Bridges the MAUI window's background/foreground callbacks into
/// <see cref="IAppLifecycleNotifier"/>, which is what lets the shared app-lock overlay re-arm.
/// <para>
/// SECURITY: a hybrid head keeps its Blazor render tree alive across a background/foreground cycle,
/// so a gate that engages only at first render never engages again. Call this from the head's
/// <c>App.CreateWindow</c> override:
/// <code>
/// protected override Window CreateWindow(IActivationState? activationState)
/// {
///     var window = base.CreateWindow(activationState);
///     window.AttachMmcaAppLifecycle(Handler!.MauiContext!.Services);
///     return window;
/// }
/// </code>
/// </para>
/// </summary>
public static class WindowLifecycleExtensions
{
    extension(Window window)
    {
        /// <summary>
        /// Forwards this window's <c>Stopped</c> and <c>Resumed</c> events to the app's
        /// <see cref="IAppLifecycleNotifier"/>. Safe to call once per window; a host that
        /// registered no notifier is a no-op.
        /// </summary>
        /// <param name="services">The app's service provider.</param>
        /// <returns>The same window, for chaining.</returns>
        public Window AttachMmcaAppLifecycle(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(services);

            var notifier = services.GetService<IAppLifecycleNotifier>();
            if (notifier is null)
            {
                return window;
            }

            window.Stopped += (_, _) => notifier.NotifyEnteredBackground();
            window.Resumed += (_, _) => notifier.NotifyResumed();

            return window;
        }
    }
}
