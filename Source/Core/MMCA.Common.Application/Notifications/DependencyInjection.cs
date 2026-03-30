using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.Application.Notifications;

/// <summary>
/// Registers Notification feature module application-layer services into the DI container.
/// All registrations use <c>TryAddScoped</c> so consuming apps can override individual handlers.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers all notification application services: command/query handlers,
        /// DTO mapper, validator, entity query service, and default recipient provider.
        /// </summary>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public IServiceCollection AddNotificationApplicationServices()
        {
            // PushNotification aggregate services
            services.TryAddScoped<INavigationPopulator<PushNotification>, NullNavigationPopulator<PushNotification>>();
            services.TryAddScoped<IEntityQueryService<PushNotification, PushNotificationDTO, PushNotificationIdentifierType>,
                EntityQueryService<PushNotification, PushNotificationDTO, PushNotificationIdentifierType>>();

            // DTO mapper
            services.TryAddScoped<PushNotificationDTOMapper>();
            services.TryAddScoped<IEntityDTOMapper<PushNotification, PushNotificationDTO, PushNotificationIdentifierType>,
                PushNotificationDTOMapper>();

            // Command handlers
            services.TryAddScoped<ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>>,
                SendPushNotificationHandler>();
            services.TryAddScoped<ICommandHandler<MarkNotificationReadCommand, Result>,
                MarkNotificationReadHandler>();
            services.TryAddScoped<ICommandHandler<MarkAllNotificationsReadCommand, Result>,
                MarkAllNotificationsReadHandler>();

            // Query handlers
            services.TryAddScoped<IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>>,
                GetNotificationHistoryHandler>();
            services.TryAddScoped<IQueryHandler<GetMyNotificationsQuery, Result<PagedCollectionResult<UserNotificationDTO>>>,
                GetMyNotificationsHandler>();
            services.TryAddScoped<IQueryHandler<GetUnreadNotificationCountQuery, Result<int>>,
                GetUnreadNotificationCountHandler>();

            // Validator
            services.AddValidatorsFromAssemblyContaining<SendPushNotificationRequestValidator>(includeInternalTypes: true);

            // Default no-op recipient provider — consuming apps register their own before this
            services.TryAddScoped<INotificationRecipientProvider, NullNotificationRecipientProvider>();

            return services;
        }
    }
}
