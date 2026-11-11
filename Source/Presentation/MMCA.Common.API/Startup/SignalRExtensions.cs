using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.API.Startup;

/// <summary>
/// Extension methods for mapping the SignalR notification hub endpoint.
/// </summary>
public static class SignalRExtensions
{
    extension(WebApplication app)
    {
        /// <summary>
        /// Maps the <see cref="NotificationHub"/> endpoint using the path configured in
        /// <see cref="PushNotificationSettings"/>. This should be called after
        /// <c>WebApplicationExtensions.UseCommonMiddlewarePipeline</c>.
        /// No-ops gracefully when push notification settings are not registered.
        /// </summary>
        public WebApplication MapNotificationHub()
        {
            var settings = app.Services.GetService<IOptions<PushNotificationSettings>>()?.Value;
            if (settings is { Enabled: true })
            {
                // Opt-in (PushNotificationSettings.CloseConnectionsOnTokenExpiry): the connection ends
                // with the token it was authenticated with, so a locked or revoked user is cut off
                // within one token lifetime. Each replica enforces it on its own connections, which
                // is why it needs neither negotiation nor a sticky ingress.
                app.MapHub<NotificationHub>(
                    settings.HubPath,
                    options => options.CloseOnAuthenticationExpiration = settings.CloseConnectionsOnTokenExpiry);
            }

            return app;
        }
    }
}
