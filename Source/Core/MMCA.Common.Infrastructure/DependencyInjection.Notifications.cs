using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Notifications.Live;
using MMCA.Common.Infrastructure.Notifications.Push;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the Notification module's EF Core entity configurations (PushNotification, UserNotification)
        /// so they are discovered during model creation.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddNotificationInfrastructure()
        {
            services.AddEntityConfigurationAssembly(
                typeof(Persistence.Configuration.EntityTypeConfiguration.Notifications.PushNotificationConfiguration).Assembly);
            return services;
        }

        /// <summary>
        /// Registers SignalR push notification services, replacing the default <see cref="NullPushNotificationSender"/>
        /// with <see cref="SignalRPushNotificationSender"/> and the default <see cref="NullLiveChannelPublisher"/>
        /// with <see cref="SignalRLiveChannelPublisher"/>. Optionally configures a Redis backplane when a Redis
        /// connection string is available.
        /// </summary>
        /// <param name="configuration">Application configuration for binding settings and detecting Redis.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddPushNotifications(IConfiguration configuration)
        {
            services.AddOptions<PushNotificationSettings>()
                .Bind(configuration.GetSection(PushNotificationSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            var signalRBuilder = services.AddSignalR();

            var redisConnectionString = configuration.GetConnectionString("redis");
            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                // SECURITY (SEC-Common-53): the backplane channel name derives from the HUB TYPE, and
                // NotificationHub ships in this framework, so every consumer application computes the
                // identical channel on a shared Redis. Without a prefix a user-targeted push or a
                // live-channel event published by one application was delivered to another
                // application's clients holding the same numeric user id. The prefix is per
                // application by default and needs no configuration.
                var channelPrefix = ApplicationNamespace.Resolve(configuration, environment: null);
                signalRBuilder.AddStackExchangeRedis(redisConnectionString, options =>
                    options.Configuration.ChannelPrefix = RedisChannel.Literal(channelPrefix));
            }

            // Replace the default Null implementations with the SignalR-backed ones.
            services.AddTransient<IPushNotificationSender, SignalRPushNotificationSender>();
            services.AddTransient<ILiveChannelPublisher, SignalRLiveChannelPublisher>();
            // Replace, not TryAdd: AddSignalR above already registered SignalR's DefaultUserIdProvider
            // (NameIdentifier only), so a TryAdd here never took effect and a principal carrying only
            // the raw sub claim got a null UserIdentifier.
            services.Replace(ServiceDescriptor.Singleton<IUserIdProvider, ClaimBasedUserIdProvider>());

            return services;
        }
    }
}
