using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// PLACEHOLDER on <c>feat/native-messaging</c>: the native Azure.Messaging.ServiceBus implementation
/// (a topic per event, a subscription per host queue forwarding to it, scheduled redelivery and the
/// native dead-letter queue) replaces <see cref="Register"/> before the branch merges. The namespace
/// and credential resolution below is kept for it.
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
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        throw new NotSupportedException("Azure Service Bus transport: native implementation pending on feat/native-messaging");
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
}
