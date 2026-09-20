using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Notifications.Live;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Infrastructure.Storage;
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
            services.TryAddSingleton<IUserIdProvider, ClaimBasedUserIdProvider>();

            return services;
        }

        /// <summary>
        /// Registers OS-level native push delivery through Azure Notification Hubs (ADR-044),
        /// replacing the default <see cref="NullNativePushSender"/>/<see cref="NullPushDeviceRegistrar"/>
        /// pair. Reads the <c>NativePush</c> section (<c>Enabled</c>, <c>ConnectionString</c>,
        /// <c>HubName</c>); when disabled or incomplete the call is a no-op, so hosts register it
        /// unconditionally and deployments switch the channel on by configuration alone (the hub
        /// itself is provisioned before its FCM/APNs credentials exist).
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>NativePush</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddNativePushNotifications(IConfiguration configuration)
        {
            services.AddOptions<NativePushSettings>()
                .Bind(configuration.GetSection(NativePushSettings.SectionName));

            var settings = configuration.GetSection(NativePushSettings.SectionName).Get<NativePushSettings>();
            if (settings is not { Enabled: true }
                || string.IsNullOrWhiteSpace(settings.ConnectionString)
                || string.IsNullOrWhiteSpace(settings.HubName))
            {
                return services;
            }

            services.TryAddSingleton<Microsoft.Azure.NotificationHubs.INotificationHubClient>(_ =>
                Microsoft.Azure.NotificationHubs.NotificationHubClient.CreateClientFromConnectionString(
                    settings.ConnectionString, settings.HubName));
            services.AddTransient<INativePushSender, AzureNotificationHubNativePushSender>();
            services.AddTransient<IPushDeviceRegistrar, AzureNotificationHubDeviceRegistrar>();

            return services;
        }

        /// <summary>
        /// Registers Azure Blob Storage as the <see cref="IFileStorageService"/> (ADR-045),
        /// replacing the unconfigured <see cref="NullFileStorageService"/> default. Reads the
        /// <c>FileStorage</c> section: <c>ServiceUri</c> (managed-identity auth via
        /// DefaultAzureCredential, the production path) or <c>ConnectionString</c> (local
        /// Azurite), plus the required <c>ContainerName</c>. An incomplete section makes this a
        /// no-op, so hosts register it unconditionally.
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>FileStorage</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAzureBlobFileStorage(IConfiguration configuration)
        {
            services.AddOptions<FileStorageSettings>()
                .Bind(configuration.GetSection(FileStorageSettings.SectionName));

            var settings = configuration.GetSection(FileStorageSettings.SectionName).Get<FileStorageSettings>();
            if (settings is null || string.IsNullOrWhiteSpace(settings.ContainerName))
            {
                return services;
            }

            // An empty-string ServiceUri binds to a RELATIVE Uri; only an absolute one counts.
            var hasServiceUri = settings.ServiceUri is { IsAbsoluteUri: true };
            if (!hasServiceUri && string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                return services;
            }

            services.TryAddSingleton(_ =>
            {
                var serviceClient = hasServiceUri
                    ? new Azure.Storage.Blobs.BlobServiceClient(settings.ServiceUri, new Azure.Identity.DefaultAzureCredential())
                    : new Azure.Storage.Blobs.BlobServiceClient(settings.ConnectionString);
                return serviceClient.GetBlobContainerClient(settings.ContainerName);
            });
            services.AddTransient<IFileStorageService, AzureBlobFileStorageService>();

            return services;
        }
    }
}
