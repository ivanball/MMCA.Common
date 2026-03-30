using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Notifications;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Notification UI module registration. Registers all notification UI services,
/// SignalR hub connection, state management, and the UI module descriptor.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers notification UI services and the <see cref="NotificationUIModule"/>
        /// for nav items and assembly discovery.
        /// </summary>
        public IServiceCollection AddNotificationUI()
        {
            // Push notification API service
            services.AddScoped<IPushNotificationUIService, PushNotificationService>();

            // Notification inbox API service
            services.AddScoped<INotificationInboxUIService, NotificationInboxService>();

            // Shared notification state for unread count (per Blazor circuit)
            services.AddScoped<NotificationState>();

            // SignalR client-side notification hub service
            services.AddScoped<NotificationHubService>();

            // Register the Notification UI module for nav items and assembly discovery
            services.AddSingleton<IUIModule, NotificationUIModule>();

            return services;
        }
    }
}
