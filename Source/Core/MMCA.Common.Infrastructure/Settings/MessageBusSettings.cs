using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Configuration for the cross-service message bus, bound from the <c>MessageBus</c> section.
/// Selects the transport implementation: <see cref="MessageBusProvider.InProcess"/> for the
/// modular monolith, <see cref="MessageBusProvider.RabbitMq"/> for development microservice
/// deployments, and <see cref="MessageBusProvider.AzureServiceBus"/> for production.
/// </summary>
public sealed class MessageBusSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "MessageBus";

    /// <summary>Gets the transport selector. Defaults to <see cref="MessageBusProvider.InProcess"/>.</summary>
    public MessageBusProvider Provider { get; init; } = MessageBusProvider.InProcess;

    /// <summary>
    /// Gets the broker connection string when <see cref="Provider"/> is
    /// <see cref="MessageBusProvider.RabbitMq"/> or <see cref="MessageBusProvider.AzureServiceBus"/>.
    /// Aspire-resourced deployments inject this via <c>ConnectionStrings:rabbitmq</c> or
    /// <c>ConnectionStrings:messaging</c>; the property is read directly so the value can come
    /// from any configuration source.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Gets the endpoint name prefix used to namespace queues per service (e.g. <c>store-catalog</c>).
    /// MassTransit appends consumer-specific suffixes; this prefix lets multiple services coexist
    /// on the same broker without colliding on queue names.
    /// </summary>
    [StringLength(64)]
    public string? EndpointPrefix { get; init; }
}

/// <summary>Available message bus transports.</summary>
public enum MessageBusProvider
{
    /// <summary>
    /// In-process dispatch via <c>InProcessMessageBus</c> — used by the modular monolith deployment.
    /// </summary>
    InProcess = 0,

    /// <summary>
    /// MassTransit on RabbitMQ — used by development microservice deployments and tests.
    /// </summary>
    RabbitMq = 1,

    /// <summary>
    /// MassTransit on Azure Service Bus — used by production microservice deployments.
    /// </summary>
    AzureServiceBus = 2,
}
