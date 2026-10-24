namespace MMCA.Common.Infrastructure.Messaging.Transports;

/// <summary>
/// The <c>MessageBus:Provider</c> values the core packages handle themselves. Any other value names
/// an <see cref="IBrokerTransport"/> an adapter package registers: <c>AzureServiceBus</c> from
/// <c>MMCA.Common.Azure</c> or <c>AmazonSqs</c> from <c>MMCA.Common.Aws</c>. Values compare
/// case-insensitively.
/// </summary>
public static class MessageBusProviders
{
    /// <summary>
    /// In-process dispatch via <c>InProcessMessageBus</c>, the modular monolith deployment and the
    /// default when the setting is absent.
    /// </summary>
    public const string InProcess = "InProcess";

    /// <summary>MassTransit on RabbitMQ, the development microservice deployment and the test tier.</summary>
    public const string RabbitMq = "RabbitMq";

    /// <summary>
    /// The adapter-supplied transports the framework knows how to point a host at, keyed by provider
    /// name: the package to reference and the call to make before <c>AddBrokerMessaging</c>. Used only
    /// to make the registration error actionable; it registers nothing.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Package, string Registration)> KnownAdapterTransports =
        new Dictionary<string, (string Package, string Registration)>(StringComparer.OrdinalIgnoreCase)
        {
            ["AzureServiceBus"] = ("MMCA.Common.Azure", "services.AddAzureServiceBusTransport()"),
            ["AmazonSqs"] = ("MMCA.Common.Aws", "services.AddAmazonSqsTransport()"),
        };

    /// <summary>Whether <paramref name="provider"/> selects in-process dispatch (blank counts as the default).</summary>
    /// <param name="provider">The configured provider name.</param>
    /// <returns><see langword="true"/> for <see cref="InProcess"/> or a blank value.</returns>
    internal static bool IsInProcess(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || string.Equals(provider, InProcess, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="provider"/> selects the built-in RabbitMQ transport.</summary>
    /// <param name="provider">The configured provider name.</param>
    /// <returns><see langword="true"/> for <see cref="RabbitMq"/>.</returns>
    internal static bool IsRabbitMq(string? provider) =>
        string.Equals(provider, RabbitMq, StringComparison.OrdinalIgnoreCase);
}
