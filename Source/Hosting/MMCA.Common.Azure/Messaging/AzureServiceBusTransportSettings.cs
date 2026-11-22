namespace MMCA.Common.Azure.Messaging;

/// <summary>
/// The Azure Service Bus transport's own keys, bound from <c>MessageBus:AzureServiceBus</c>: the
/// namespace a host signs in to with a Microsoft Entra token instead of a shared access key.
/// </summary>
internal sealed class AzureServiceBusTransportSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionName = "MessageBus:AzureServiceBus";

    /// <summary>
    /// The fully qualified namespace, for example <c>contoso.servicebus.windows.net</c> (an
    /// <c>sb://</c> address is accepted too). When set, the bus signs in with a token credential and
    /// any broker connection string is ignored.
    /// </summary>
    public string? FullyQualifiedNamespace { get; init; }

    /// <summary>
    /// The client id of the user-assigned managed identity to sign in as. Blank selects the
    /// system-assigned identity (or the one <c>AZURE_CLIENT_ID</c> names). Read only with
    /// <see cref="FullyQualifiedNamespace"/>.
    /// </summary>
    public string? ManagedIdentityClientId { get; init; }
}
