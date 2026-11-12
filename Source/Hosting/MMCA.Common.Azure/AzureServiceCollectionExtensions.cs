using System.Diagnostics.CodeAnalysis;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Azure.PostgreSql;
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

        /// <summary>
        /// Puts every PostgreSQL data source on Microsoft Entra token login for Azure Database for
        /// PostgreSQL when <c>AzurePostgreSql:EntraAuthentication:Enabled</c> is true: Npgsql's periodic
        /// password provider sends an Entra access token for the
        /// <c>https://ossrdbms-aad.database.windows.net/.default</c> scope from
        /// <c>DefaultAzureCredential</c> (a managed identity in Azure; a user-assigned one selects
        /// itself through <c>AZURE_CLIENT_ID</c>), refreshed every four minutes, inside the credential's
        /// five-minute refresh margin. Off (absent or false), this registers nothing. Order-independent
        /// relative to <c>AddInfrastructure</c>.
        /// <para>
        /// The connection strings carry no password, set <c>Username</c> to the Entra principal name, and
        /// require TLS (<c>SSL Mode=Require</c>, <c>VerifyCA</c> or <c>VerifyFull</c>), because the token
        /// is sent as the password: a configured PostgreSQL connection string that does not fails
        /// registration naming its key, and one supplied later fails when its data source is built. A
        /// connection string that also carries a static <c>Password</c> is refused by Npgsql when the
        /// data source is built. SQL Server, SQLite and Cosmos sources are untouched (SQL Server takes
        /// Entra through the SqlClient extension this package already carries).
        /// </para>
        /// </summary>
        /// <param name="configuration">Application configuration providing <c>AzurePostgreSql:EntraAuthentication</c> and the connection strings.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// A configured PostgreSQL connection string does not require TLS (the message names its key).
        /// </exception>
        public IServiceCollection AddAzurePostgreSqlEntraAuthentication(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var settings = configuration
                .GetSection(AzurePostgreSqlEntraAuthenticationSettings.SectionPath)
                .Get<AzurePostgreSqlEntraAuthenticationSettings>();
            if (settings is not { Enabled: true })
            {
                return services;
            }

            // Deterministic: a connection string already in configuration that would send the token
            // in clear fails here, at registration, not on the first query.
            AzurePostgreSqlEntraAuthenticationConfigurator.EnsureConfiguredConnectionStringsAreEncrypted(configuration);

            // The receipt the core startup tripwire checks: the gate set without this call fails the
            // host instead of letting it log in with whatever password the connection string carries.
            if (!services.Any(static descriptor =>
                    descriptor.ServiceType == typeof(HostIntegrationMarker)
                    && descriptor.ImplementationInstance is HostIntegrationMarker marker
                    && string.Equals(marker.Name, HostIntegrationNames.AzurePostgreSqlEntraAuthentication, StringComparison.Ordinal)))
            {
                services.AddSingleton(new HostIntegrationMarker(HostIntegrationNames.AzurePostgreSqlEntraAuthentication));
            }

            return services.AddNpgsqlDataSourceConfigurator(new AzurePostgreSqlEntraAuthenticationConfigurator());
        }
    }
}
