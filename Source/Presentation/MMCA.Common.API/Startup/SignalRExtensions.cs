using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Hubs;
using MMCA.Common.Infrastructure.Settings;

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
        /// <see cref="WebApplicationExtensions.UseCommonMiddlewarePipeline"/>.
        /// No-ops gracefully when push notification settings are not registered.
        /// </summary>
        public WebApplication MapNotificationHub()
        {
            var settings = app.Services.GetService<IOptions<PushNotificationSettings>>()?.Value;
            if (settings is { Enabled: true })
            {
                app.MapHub<NotificationHub>(settings.HubPath);
            }

            return app;
        }
    }
}
