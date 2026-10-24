using System.Diagnostics.CodeAnalysis;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Azure.Storage;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Azure;

/// <summary>
/// Service registrations for the Azure implementations of the framework's cloud-neutral
/// abstractions. Each call swaps one inert core default (<c>NullFileStorageService</c>,
/// <c>NullNativePushSender</c> / <c>NullPushDeviceRegistrar</c>) for its Azure implementation, and
/// each is a no-op when its configuration section is incomplete, so hosts register them
/// unconditionally and deployments switch the capability on by configuration alone.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class AzureServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers Azure Blob Storage as the <see cref="IFileStorageService"/> (ADR-045),
        /// replacing the unconfigured core default. Reads the <c>FileStorage</c> section:
        /// <c>ServiceUri</c> (managed-identity auth via DefaultAzureCredential, the production path)
        /// or <c>ConnectionString</c> (local Azurite), plus the required <c>ContainerName</c>. An
        /// incomplete section makes this a no-op, so hosts register it unconditionally.
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>FileStorage</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAzureBlobFileStorage(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<AzureBlobFileStorageSettings>()
                .Bind(configuration.GetSection(AzureBlobFileStorageSettings.SectionName));

            var settings = configuration.GetSection(AzureBlobFileStorageSettings.SectionName).Get<AzureBlobFileStorageSettings>();
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
                    ? new BlobServiceClient(settings.ServiceUri, new DefaultAzureCredential())
                    : new BlobServiceClient(settings.ConnectionString);
                return serviceClient.GetBlobContainerClient(settings.ContainerName);
            });
            services.AddTransient<IFileStorageService, AzureBlobFileStorageService>();

            return services;
        }

        /// <summary>
        /// Registers OS-level native push delivery through Azure Notification Hubs (ADR-044),
        /// replacing the core <c>NullNativePushSender</c> / <c>NullPushDeviceRegistrar</c> pair. Reads
        /// the <c>NativePush</c> section (<c>Enabled</c>, <c>ConnectionString</c>, <c>HubName</c>);
        /// when disabled or incomplete the call is a no-op, so hosts register it unconditionally and
        /// deployments switch the channel on by configuration alone (the hub itself is provisioned
        /// before its FCM/APNs credentials exist).
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>NativePush</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAzureNotificationHubsPush(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<AzureNotificationHubSettings>()
                .Bind(configuration.GetSection(AzureNotificationHubSettings.SectionName));

            var settings = configuration.GetSection(AzureNotificationHubSettings.SectionName).Get<AzureNotificationHubSettings>();
            if (settings is not { Enabled: true }
                || string.IsNullOrWhiteSpace(settings.ConnectionString)
                || string.IsNullOrWhiteSpace(settings.HubName))
            {
                return services;
            }

            services.TryAddSingleton<INotificationHubClient>(_ =>
                NotificationHubClient.CreateClientFromConnectionString(
                    settings.ConnectionString, settings.HubName));
            services.AddTransient<INativePushSender, AzureNotificationHubNativePushSender>();
            services.AddTransient<IPushDeviceRegistrar, AzureNotificationHubDeviceRegistrar>();

            return services;
        }

        /// <summary>
        /// Registers the Azure Service Bus broker transport, so <c>AddBrokerMessaging</c> can select it
        /// with <c>MessageBus:Provider=AzureServiceBus</c>. Call it BEFORE <c>AddBrokerMessaging</c>,
        /// which picks the transport while it registers the bus. The connection string resolves the
        /// usual way (<c>MessageBus:ConnectionString</c>, then <c>ConnectionStrings:messaging</c>); one
        /// carrying <c>UseDevelopmentEmulator=true</c> targets the local emulator and needs
        /// <c>MessageBus:EmulatorAdminEndpoint</c>. Registration only: a host whose provider is
        /// something else is unaffected.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAzureServiceBusTransport() =>
            services.AddBrokerTransport(new AzureServiceBusTransport());
    }
}
