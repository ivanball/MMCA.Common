using MMCA.Common.UI.Services.Capabilities;
using Plugin.LocalNotification;
using Plugin.LocalNotification.EventArgs;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// Runs when the MAUI app is built: bridges notification taps into
/// <see cref="IDeepLinkDispatcher"/>. A tap on a scheduled reminder carries its
/// app-relative route in <c>ReturningData</c>; publishing it here lets the shared
/// <c>DeepLinkListener</c> component navigate once the Blazor router is alive — including
/// cold starts, where the dispatcher buffers the route until first render.
/// </summary>
public sealed class DeviceCapabilitiesInitializer : IMauiInitializeService
{
    /// <inheritdoc />
    public void Initialize(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var dispatcher = services.GetService<IDeepLinkDispatcher>();
        if (dispatcher is null)
        {
            return;
        }

        LocalNotificationCenter.Current.NotificationActionTapped += args => OnNotificationTapped(dispatcher, args);
    }

    private static void OnNotificationTapped(IDeepLinkDispatcher dispatcher, NotificationActionEventArgs args)
    {
        if (args.IsDismissed)
        {
            return;
        }

        var route = args.Request?.ReturningData;
        if (!string.IsNullOrWhiteSpace(route))
        {
            dispatcher.Publish(route);
        }
    }
}
