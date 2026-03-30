using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Controllers.Notifications;

namespace MMCA.Common.API.Notifications;

/// <summary>
/// Notification module API-layer DI extensions.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the Common notification API controllers to the MVC application parts so they are
    /// discoverable by ASP.NET Core routing. Required because these controllers reside in the
    /// MMCA.Common.API NuGet assembly, which is not scanned by default.
    /// </summary>
    /// <param name="builder">The MVC builder to configure.</param>
    /// <returns>The MVC builder for chaining.</returns>
    public static IMvcBuilder AddNotificationControllers(this IMvcBuilder builder)
    {
        builder.AddApplicationPart(typeof(NotificationsController).Assembly);
        return builder;
    }
}
