using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// Where the transport connects, resolved and validated while <c>AddBrokerMessaging</c> runs: either a
/// fully qualified namespace with a Microsoft Entra credential, or a connection string (plus, for the
/// local emulator, the management-plane connection string). Creating the clients performs no I/O.
/// A class rather than a record on purpose: a record's generated <c>ToString</c> would print the
/// shared access key of a connection string into any log line that formats it.
/// </summary>
internal sealed class AzureServiceBusEndpoint
{
    /// <summary>Initializes a new instance of the <see cref="AzureServiceBusEndpoint"/> class.</summary>
    /// <param name="fullyQualifiedNamespace">The namespace host name for the token sign-in, or <see langword="null"/>.</param>
    /// <param name="credential">The Entra credential for <paramref name="fullyQualifiedNamespace"/>, or <see langword="null"/>.</param>
    /// <param name="connectionString">The data-plane connection string when no namespace is configured, or <see langword="null"/>.</param>
    /// <param name="administrationConnectionString">
    /// The management-plane connection string: the data-plane one for a real namespace, the
    /// admin-endpoint form for the emulator, or <see langword="null"/> with a namespace.
    /// </param>
    /// <param name="isEmulator">Whether the connection string targets the local emulator.</param>
    public AzureServiceBusEndpoint(
        string? fullyQualifiedNamespace,
        TokenCredential? credential,
        string? connectionString,
        string? administrationConnectionString,
        bool isEmulator)
    {
        FullyQualifiedNamespace = fullyQualifiedNamespace;
        Credential = credential;
        ConnectionString = connectionString;
        AdministrationConnectionString = administrationConnectionString;
        IsEmulator = isEmulator;
    }

    /// <summary>Gets the namespace host name for the token sign-in, or <see langword="null"/>.</summary>
    public string? FullyQualifiedNamespace { get; }

    /// <summary>Gets the Entra credential, or <see langword="null"/> on the connection-string path.</summary>
    public TokenCredential? Credential { get; }

    /// <summary>Gets the data-plane connection string, or <see langword="null"/> on the token path.</summary>
    public string? ConnectionString { get; }

    /// <summary>Gets the management-plane connection string, or <see langword="null"/> on the token path.</summary>
    public string? AdministrationConnectionString { get; }

    /// <summary>Gets a value indicating whether the connection string targets the local emulator.</summary>
    public bool IsEmulator { get; }

    /// <summary>Creates the data-plane client.</summary>
    /// <returns>A new client; the caller owns it.</returns>
    internal ServiceBusClient CreateClient() =>
        FullyQualifiedNamespace is not null
            ? new ServiceBusClient(FullyQualifiedNamespace, Credential)
            : new ServiceBusClient(ConnectionString);

    /// <summary>Creates the management-plane client the topology is provisioned through.</summary>
    /// <returns>A new administration client.</returns>
    internal ServiceBusAdministrationClient CreateAdministrationClient() =>
        FullyQualifiedNamespace is not null
            ? new ServiceBusAdministrationClient(FullyQualifiedNamespace, Credential)
            : new ServiceBusAdministrationClient(AdministrationConnectionString);
}
