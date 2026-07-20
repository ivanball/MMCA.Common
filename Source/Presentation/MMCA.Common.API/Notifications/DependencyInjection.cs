using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Controllers.Notifications;

namespace MMCA.Common.API.Notifications;

/// <summary>
/// Notification module API-layer DI extensions.
/// </summary>
public static class DependencyInjection
{
    extension(IMvcBuilder builder)
    {
        /// <summary>
        /// Adds the Common notification API controllers to the MVC application parts so they are
        /// discoverable by ASP.NET Core routing. Required because these controllers reside in the
        /// MMCA.Common.API NuGet assembly, which is not scanned by default.
        /// </summary>
        /// <returns>The MVC builder for chaining.</returns>
        public IMvcBuilder AddNotificationControllers()
        {
            builder.AddApplicationPart(typeof(NotificationsController).Assembly);
            return builder;
        }
    }
}
