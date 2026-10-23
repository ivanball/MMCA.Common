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
        /// Adds the Common notification API controllers to the MVC application parts. This makes the
        /// dependency explicit rather than enabling something otherwise absent: a host that references
        /// MMCA.Common.API usually discovers these controllers already, because the SDK-generated
        /// <c>MvcApplicationPartsAssemblyInfo</c> lists every referenced assembly that depends on MVC,
        /// so they are routed and appear in the host's OpenAPI document even without this call.
        /// Adding the part again is a no-op. Exposing the endpoints is not enough to serve them: the
        /// host must also call <c>AddNotificationApplicationServices</c>, or the inbox endpoints fail
        /// to resolve their handlers.
        /// </summary>
        /// <returns>The MVC builder for chaining.</returns>
        public IMvcBuilder AddNotificationControllers()
        {
            builder.AddApplicationPart(typeof(NotificationsController).Assembly);
            return builder;
        }
    }
}
