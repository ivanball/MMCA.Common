using Azure.Core;
using MassTransit;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Azure Service Bus broker transport (<c>MessageBus:Provider=AzureServiceBus</c>), registered by
/// <c>AddAzureServiceBusTransport()</c> and selected by <c>AddBrokerMessaging</c>.
/// <para>
/// Two ways onto a namespace. <c>MessageBus:AzureServiceBus:FullyQualifiedNamespace</c> signs in with a
/// Microsoft Entra token from <c>DefaultAzureCredential</c> (pinned to the user-assigned managed
/// identity named by <c>MessageBus:AzureServiceBus:ManagedIdentityClientId</c> when it is set), so the
/// namespace can run with shared access keys switched off; it wins when a connection string is also
/// present. Otherwise the resolved broker connection string is used exactly as before. With neither,
/// registration fails naming both.
/// </para>
/// <para>
/// Second-level delayed redelivery is applied UNCONDITIONALLY: Azure Service Bus schedules messages
/// natively, so there is no plugin to install and no configuration in which it can fail at bus start.
/// <c>MessageBus:EnableDelayedRedelivery</c> is deliberately not consulted here, because it exists
/// only to gate the RabbitMQ plugin requirement.
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
    /// <exception cref="InvalidOperationException">
    /// Neither a fully qualified namespace nor a connection string is configured, or the namespace is
    /// not a host name.
    /// </exception>
    public void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Settings;
        var connectionString = context.ConnectionString;
        var serviceBusSettings = context.Configuration.GetSection(AzureServiceBusTransportSettings.SectionName).Get<AzureServiceBusTransportSettings>()
            ?? new AzureServiceBusTransportSettings();

        // Checked HERE, while AddBrokerMessaging runs, rather than inside the bus-factory callback,
        // which only runs when the bus is first resolved.
        var namespaceAddress = ResolveNamespaceAddress(serviceBusSettings.FullyQualifiedNamespace);
        if (namespaceAddress is null && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"MessageBus:Provider is '{ProviderName}', but no namespace is configured. Set {FullyQualifiedNamespaceKey} "
                + "(for example 'contoso.servicebus.windows.net') to sign in with a Microsoft Entra token (managed identity, optionally "
                + $"{AzureServiceBusTransportSettings.SectionName}:ManagedIdentityClientId), or a connection string "
                + "(MessageBus:ConnectionString, ConnectionStrings:rabbitmq or ConnectionStrings:messaging).");
        }

        var credential = namespaceAddress is null ? null : _credentialFactory(serviceBusSettings.ManagedIdentityClientId);

        configurator.UsingAzureServiceBus((busContext, cfg) =>
        {
            if (namespaceAddress is not null)
            {
                cfg.Host(namespaceAddress, h => h.TokenCredential = credential);
            }
            else if (ServiceBusEmulatorSupport.IsEmulatorConnectionString(connectionString))
            {
                // The emulator branch exists so a development stack can run the SAME transport
                // production runs. It is entered only when the connection string carries
                // UseDevelopmentEmulator=true, a token no real namespace emits, so the production
                // connection-string path below is a single unconditional Host(connectionString).
                ServiceBusEmulatorSupport.ConfigureEmulatorHost(
                    cfg, connectionString!, settings.EmulatorAdminEndpoint);
            }
            else
            {
                cfg.Host(connectionString);
            }

            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery: true);
            cfg.ConfigureEndpoints(busContext);
        });
    }

    /// <summary>
    /// The bus host address for a configured namespace: <c>sb://{namespace}/</c>, or
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
}
