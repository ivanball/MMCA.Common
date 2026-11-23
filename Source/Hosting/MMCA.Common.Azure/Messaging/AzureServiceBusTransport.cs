using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Azure Service Bus broker transport (<c>MessageBus:Provider=AzureServiceBus</c>), registered by
/// <c>AddAzureServiceBusTransport()</c> and selected by <c>AddBrokerMessaging</c>, on the
/// Azure.Messaging.ServiceBus SDK alone.
/// <para>
/// Topology: a topic per event (<see cref="AzureServiceBusEntityNames.Topic"/>), the host's queue
/// (<see cref="AzureServiceBusEntityNames.Queue"/>), and on each consumed topic one subscription named
/// after the queue (<see cref="AzureServiceBusEntityNames.Subscription"/>) that forwards every message
/// to it. With <c>MessageBus:ProvisionTopology</c> on (the default) the receiver creates them
/// idempotently when it starts and the publisher creates a missing topic on its first publish.
/// Redelivery schedules a copy on the queue (Service Bus schedules arbitrarily far ahead, so
/// <see cref="MaxRedeliveryDelay"/> is <see langword="null"/>), and dead-lettering uses the queue's
/// native dead-letter queue.
/// </para>
/// <para>
/// Two ways onto a namespace. <c>MessageBus:AzureServiceBus:FullyQualifiedNamespace</c> signs in with a
/// Microsoft Entra token from <c>DefaultAzureCredential</c> (pinned to the user-assigned managed
/// identity named by <c>MessageBus:AzureServiceBus:ManagedIdentityClientId</c> when it is set), so the
/// namespace can run with shared access keys switched off; it wins when a connection string is also
/// present. Otherwise the resolved broker connection string is used. With neither, registration fails
/// naming both. A connection string carrying <c>UseDevelopmentEmulator=true</c> targets the local
/// emulator, whose management plane is reached through <c>MessageBus:EmulatorAdminEndpoint</c>
/// (<see cref="ServiceBusEmulatorSupport"/>).
/// </para>
/// </summary>
internal sealed class AzureServiceBusTransport : IBrokerTransport
{
    /// <summary>The <c>MessageBus:Provider</c> value that selects this transport.</summary>
    internal const string ProviderName = "AzureServiceBus";

    /// <summary>The configuration key of the namespace the token sign-in targets.</summary>
    internal const string FullyQualifiedNamespaceKey = AzureServiceBusTransportSettings.SectionName + ":FullyQualifiedNamespace";

    private readonly Func<string?, TokenCredential> _credentialFactory;

    /// <summary>Initializes a new instance that signs in through <see cref="AzureTokenCredentials"/>.</summary>
    public AzureServiceBusTransport()
        : this(AzureTokenCredentials.Create)
    {
    }

    /// <summary>Initializes a new instance with an explicit credential factory (tests).</summary>
    /// <param name="credentialFactory">Creates the credential for the configured managed identity client id.</param>
    internal AzureServiceBusTransport(Func<string?, TokenCredential> credentialFactory) =>
        _credentialFactory = credentialFactory ?? throw new ArgumentNullException(nameof(credentialFactory));

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public TimeSpan? MaxRedeliveryDelay => null;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Neither a fully qualified namespace nor a connection string is configured, the namespace is not
    /// a host name, or an emulator connection string has no valid <c>MessageBus:EmulatorAdminEndpoint</c>.
    /// </exception>
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        // Resolved HERE, while AddBrokerMessaging runs, so a configuration error fails startup rather
        // than the first publish.
        var endpoint = ResolveEndpoint(context);
        var settings = context.Settings;
        var topology = context.Topology;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new AzureServiceBusConnection(endpoint));

        if (settings.ProvisionTopology)
        {
            services.AddSingleton(sp => new AzureServiceBusTopologyProvisioner(
                sp.GetRequiredService<AzureServiceBusConnection>().Administration,
                endpoint.IsEmulator,
                LoggerFor<AzureServiceBusTopologyProvisioner>(sp)));
        }

        services.AddSingleton<IBrokerPublisher>(sp => new AzureServiceBusPublisher(
            sp.GetRequiredService<AzureServiceBusConnection>().Client,
            sp.GetService<AzureServiceBusTopologyProvisioner>()));

        services.AddHostedService(sp => new AzureServiceBusReceiver(
            sp.GetRequiredService<AzureServiceBusConnection>(),
            topology,
            new AzureServiceBusDeliveryHandler(
                sp.GetRequiredService<IBrokerReceivePipeline>(),
                sp.GetRequiredService<TimeProvider>(),
                LoggerFor<AzureServiceBusDeliveryHandler>(sp)),
            sp.GetService<AzureServiceBusTopologyProvisioner>(),
            settings,
            LoggerFor<AzureServiceBusReceiver>(sp)));
    }

    /// <summary>
    /// Resolves where the transport connects: the token-signed namespace when one is configured,
    /// otherwise the connection string, with the emulator's management-plane connection string derived
    /// from <c>MessageBus:EmulatorAdminEndpoint</c> when the connection string targets the emulator.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <returns>The validated endpoint.</returns>
    /// <exception cref="InvalidOperationException">The configuration names no usable namespace.</exception>
    internal AzureServiceBusEndpoint ResolveEndpoint(BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var serviceBusSettings = ReadSettings(context.Configuration);
        var connectionString = context.ConnectionString;

        var namespaceAddress = ResolveNamespaceAddress(serviceBusSettings.FullyQualifiedNamespace);
        if (namespaceAddress is not null)
        {
            return new AzureServiceBusEndpoint(
                namespaceAddress.Host,
                CreateCredential(serviceBusSettings.ManagedIdentityClientId),
                connectionString: null,
                administrationConnectionString: null,
                isEmulator: false);
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"MessageBus:Provider is '{ProviderName}', but no namespace is configured. Set {FullyQualifiedNamespaceKey} "
                + "(for example 'contoso.servicebus.windows.net') to sign in with a Microsoft Entra token (managed identity, optionally "
                + $"{AzureServiceBusTransportSettings.SectionName}:ManagedIdentityClientId), or a connection string "
                + "(MessageBus:ConnectionString, ConnectionStrings:rabbitmq or ConnectionStrings:messaging).");
        }

        // The emulator branch exists so a development stack can run the SAME transport production
        // runs. It is entered only when the connection string carries UseDevelopmentEmulator=true, a
        // token no real namespace emits.
        if (ServiceBusEmulatorSupport.IsEmulatorConnectionString(connectionString))
        {
            return new AzureServiceBusEndpoint(
                fullyQualifiedNamespace: null,
                credential: null,
                connectionString,
                ServiceBusEmulatorSupport.BuildAdminConnectionString(connectionString, context.Settings.EmulatorAdminEndpoint),
                isEmulator: true);
        }

        return new AzureServiceBusEndpoint(
            fullyQualifiedNamespace: null,
            credential: null,
            connectionString,
            connectionString,
            isEmulator: false);
    }

    /// <summary>Creates the Entra credential for the configured managed identity, through the injected factory.</summary>
    /// <param name="managedIdentityClientId">The user-assigned managed identity client id, or <see langword="null"/>.</param>
    /// <returns>The token credential.</returns>
    internal TokenCredential CreateCredential(string? managedIdentityClientId) => _credentialFactory(managedIdentityClientId);

    /// <summary>Binds <c>MessageBus:AzureServiceBus</c>, or the defaults when the section is absent.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The bound settings.</returns>
    internal static AzureServiceBusTransportSettings ReadSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetSection(AzureServiceBusTransportSettings.SectionName).Get<AzureServiceBusTransportSettings>()
            ?? new AzureServiceBusTransportSettings();
    }

    /// <summary>
    /// The namespace host address for a configured namespace: <c>sb://{namespace}/</c>, or
    /// <see langword="null"/> when none is configured.
    /// </summary>
    /// <param name="fullyQualifiedNamespace">A host name such as <c>contoso.servicebus.windows.net</c>, or an <c>sb://</c> address.</param>
    /// <returns>The host address, or <see langword="null"/> when blank.</returns>
    /// <exception cref="InvalidOperationException">The value is neither a host name nor an <c>sb://</c> address.</exception>
    internal static Uri? ResolveNamespaceAddress(string? fullyQualifiedNamespace)
    {
        if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
        {
            return null;
        }

        var value = fullyQualifiedNamespace.Trim();
        var host = value.Contains("://", StringComparison.Ordinal) ? HostOfServiceBusAddress(value) : value.TrimEnd('/');

        if (host is null || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            throw new InvalidOperationException(
                $"{FullyQualifiedNamespaceKey} is '{value}', which is not a Service Bus namespace. Set it to the namespace host name, "
                + "for example 'contoso.servicebus.windows.net' (or 'sb://contoso.servicebus.windows.net/').");
        }

        return new Uri($"sb://{host}/");
    }

    private static string? HostOfServiceBusAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var address)
        && string.Equals(address.Scheme, "sb", StringComparison.OrdinalIgnoreCase)
            ? address.Host
            : null;

    private static ILogger LoggerFor<T>(IServiceProvider services) =>
        (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<T>();
}
