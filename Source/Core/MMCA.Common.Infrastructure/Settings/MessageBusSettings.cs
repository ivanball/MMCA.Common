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
    /// When set, the broker registration installs a kebab-case endpoint name formatter carrying this
    /// prefix, so a consumer named <c>OrderPlacedConsumer</c> in a service prefixed
    /// <c>store-catalog</c> binds the queue <c>store-catalog-order-placed</c> instead of the bare
    /// <c>order-placed</c>. That is what lets several services with the same consumer type names
    /// coexist on one broker without silently sharing a queue (competing consumers across service
    /// boundaries, where each event reaches only one of them).
    /// <para>
    /// The consumer's namespace is deliberately left out of the generated name: the prefix is the
    /// only namespacing applied, so a queue name stays readable and survives a type moving between
    /// folders. Leave the setting unset and endpoint names are formatted by MassTransit's default,
    /// which is the right choice for a single service on its own broker.
    /// </para>
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
    /// Gets the explicit override for the consumer-side idempotency inbox, or <see langword="null"/>
    /// (the default) to let the selected transport decide. When enabled, the integration-event
    /// consumers dedup already-processed messages via the <c>InboxMessages</c> table in the
    /// consumer's database. The table is part of the shared relational model (created by the
    /// standard migrations; Cosmos hosts skip it), so turning it on for a migrated relational host
    /// needs no schema work.
    /// <para>
    /// Leave it unset and <see cref="IsInboxEnabled"/> resolves it from
    /// <see cref="Provider"/>: ON for a broker (<see cref="MessageBusProvider.RabbitMq"/>,
    /// <see cref="MessageBusProvider.AzureServiceBus"/>), OFF for
    /// <see cref="MessageBusProvider.InProcess"/>, which has no redelivery to dedup. Broker
    /// delivery is at-least-once by contract: a consumer that acks after a network blip, a
    /// redelivered message after a lease expiry, or an outbox row republished after a crash all
    /// hand the same event to the same handlers twice, and with the inbox off every one of those
    /// becomes a duplicate side effect (a second email, a second charge attempt, a double
    /// decrement) unless every handler happens to be idempotent on its own. That is why the broker
    /// default is ON rather than the safe-looking OFF.
    /// </para>
    /// <para>
    /// An explicit value always wins in both directions. A host that must not query the
    /// <c>InboxMessages</c> table (it has not run the migration yet) sets
    /// <c>MessageBus:EnableInbox=false</c> and gets one startup Warning
    /// (<c>InboxDisabledWarningService</c>) recording the opt-out rather than silence.
    /// </para>
    /// </summary>
    public bool? EnableInbox { get; init; }

    /// <summary>
    /// Gets the RESOLVED inbox posture: the explicit <see cref="EnableInbox"/> value when the host
    /// set one, otherwise <see langword="true"/> for a broker transport and <see langword="false"/>
    /// for <see cref="MessageBusProvider.InProcess"/>. Every framework component reads this rather
    /// than the raw setting.
    /// </summary>
    public bool IsInboxEnabled => EnableInbox ?? Provider != MessageBusProvider.InProcess;

    /// <summary>
    /// Gets a value indicating whether second-level (broker-scheduled) redelivery is applied on
    /// top of the in-process <c>UseMessageRetry</c> policy. When <see langword="true"/>, a message
    /// that exhausts its immediate retries is scheduled back onto the queue after each interval in
    /// <see cref="RedeliveryIntervalsSeconds"/> instead of dead-lettering right away, which is what
    /// carries a consumer through an outage measured in minutes or hours rather than seconds.
    /// <para>
    /// Defaults to <see langword="false"/> because on RabbitMQ this requires the
    /// <c>rabbitmq_delayed_message_exchange</c> plugin, and the Aspire development RabbitMQ
    /// container does not ship it: enabling it against a plugin-less broker fails at bus start.
    /// Set it to <see langword="true"/> only on a broker where the plugin is installed.
    /// </para>
    /// <para>
    /// This flag is IGNORED by <see cref="MessageBusProvider.AzureServiceBus"/>, which supports
    /// scheduled redelivery natively and therefore always applies
    /// <see cref="RedeliveryIntervalsSeconds"/>.
    /// </para>
    /// </summary>
    public bool EnableDelayedRedelivery { get; init; }

    /// <summary>
    /// Gets the second-level redelivery intervals, in seconds. Each entry is one scheduled
    /// redelivery attempt after the in-process retry policy is exhausted; the message
    /// dead-letters only after the last interval also fails. Defaults to
    /// <c>[60, 600, 3600]</c> (one minute, ten minutes, one hour), a spread wide enough to ride
    /// out a dependency restart, a failover and a short incident without an operator replaying
    /// the error queue by hand.
    /// <para>
    /// Applied unconditionally on <see cref="MessageBusProvider.AzureServiceBus"/> (native
    /// scheduled delivery) and only when <see cref="EnableDelayedRedelivery"/> is
    /// <see langword="true"/> on <see cref="MessageBusProvider.RabbitMq"/> (needs the
    /// delayed-message-exchange plugin).
    /// </para>
    /// </summary>
    public IReadOnlyList<int> RedeliveryIntervalsSeconds { get; init; } = [60, 600, 3600];
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
