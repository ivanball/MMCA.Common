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

    /// <summary>
    /// Gets the maximum number of in-process redelivery attempts MassTransit makes (via
    /// <c>UseMessageRetry</c>) before a faulted message is moved to the <c>_error</c> queue.
    /// Applies to every broker receive endpoint. Set to <c>0</c> to disable retries.
    /// Defaults to <c>5</c>.
    /// </summary>
    [Range(0, 20)]
    public int RetryLimit { get; init; } = 5;

    /// <summary>
    /// Gets the first retry interval, in seconds. Subsequent intervals grow exponentially up to
    /// <see cref="RetryMaxIntervalSeconds"/>. Defaults to <c>1</c>.
    /// </summary>
    [Range(0, 300)]
    public int RetryMinIntervalSeconds { get; init; } = 1;

    /// <summary>
    /// Gets the cap on the exponential retry interval, in seconds. Defaults to <c>30</c>.
    /// </summary>
    [Range(0, 3600)]
    public int RetryMaxIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Gets a value indicating whether the consumer-side idempotency inbox is enabled. When
    /// <see langword="true"/>, <c>IntegrationEventConsumer</c> dedups already-processed messages via
    /// an <c>InboxMessages</c> table in the consumer's database — which requires that table to exist
    /// (apply the <c>AddInboxMessages</c> migration). Defaults to <see langword="false"/>.
    /// </summary>
    public bool EnableInbox { get; init; }
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
