using System.ComponentModel.DataAnnotations;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Infrastructure.Messaging;

/// <summary>
/// Configuration for the cross-service message bus, bound from the <c>MessageBus</c> section.
/// Selects the transport implementation: <see cref="MessageBusProviders.InProcess"/> for the
/// modular monolith, <see cref="MessageBusProviders.RabbitMq"/> for development microservice
/// deployments, and an adapter-registered broker (<c>AzureServiceBus</c> from
/// <c>MMCA.Common.Azure</c>, <c>AmazonSqs</c> from <c>MMCA.Common.Aws</c>) for production.
/// </summary>
public sealed class MessageBusSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "MessageBus";

    /// <summary>
    /// Gets the transport selector, compared case-insensitively. Defaults to
    /// <see cref="MessageBusProviders.InProcess"/>. <see cref="MessageBusProviders.InProcess"/> and
    /// <see cref="MessageBusProviders.RabbitMq"/> and <see cref="MessageBusProviders.PostgreSql"/> are built in; any other value names an
    /// <see cref="IBrokerTransport"/> an adapter package registers before <c>AddBrokerMessaging</c>
    /// (e.g. <c>AzureServiceBus</c> through <c>AddAzureServiceBusTransport()</c>).
    /// </summary>
    public string Provider { get; init; } = MessageBusProviders.InProcess;

    /// <summary>
    /// Gets the broker connection string when <see cref="Provider"/> names a broker transport.
    /// Aspire-resourced deployments inject this via <c>ConnectionStrings:rabbitmq</c> or
    /// <c>ConnectionStrings:messaging</c>; the property is read directly so the value can come
    /// from any configuration source.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Gets the base address of the Azure Service Bus emulator's HTTP management plane, for example
    /// <c>http://localhost:5300</c>. Set ONLY by a development stack pointing at the local emulator;
    /// leave it unset for a real Azure Service Bus namespace, which serves both planes on one
    /// endpoint and needs nothing here.
    /// <para>
    /// The emulator serves the data plane (AMQP) and the management plane (HTTP) on two different
    /// ports, so the administration client that provisions topics, subscriptions and queues cannot be
    /// derived from <see cref="ConnectionString"/> alone. An Aspire AppHost gets this value for free
    /// from the emulator <c>WithBroker</c> overload.
    /// </para>
    /// <para>
    /// The setting is read only when <see cref="ConnectionString"/> carries
    /// <c>UseDevelopmentEmulator=true</c>; when it does, a missing or malformed value fails at
    /// registration rather than at the first publish, because a transport with no management client
    /// cannot provision the topology it needs to run at all.
    /// </para>
    /// </summary>
    [StringLength(2048)]
    public string? EmulatorAdminEndpoint { get; init; }

    /// <summary>
    /// Gets the name of the ONE queue this host receives on, or <see langword="null"/> (the default)
    /// to derive it from the host application name: lower kebab case with a trailing
    /// <c>-service</c> removed, so <c>MMCA.ADC.Engagement.Service</c> receives on
    /// <c>mmca-adc-engagement</c> (see <c>BrokerNaming.Queue</c>). Every integration event the host
    /// consumes is bound to this queue, so two services consuming the same event each get their own
    /// copy instead of competing for one.
    /// <para>
    /// Set it explicitly only to pin a name that must survive a host rename, or when two hosts of the
    /// same service must share one queue as competing consumers (replicas always do, because they run
    /// under the same application name). A configured value is converted to kebab case but never
    /// trimmed; a transport whose broker limits name length shortens it deterministically.
    /// </para>
    /// </summary>
    [StringLength(50)]
    public string? QueueName { get; init; }

    /// <summary>
    /// Gets a value indicating whether the transport creates the broker entities it needs: the topic
    /// per published or consumed event, this host's queue, and the topic-to-queue bindings. Creation
    /// is idempotent and happens when the receiver starts (and on the first publish to a missing
    /// topic). Defaults to <see langword="true"/>.
    /// <para>
    /// Set it to <see langword="false"/> where the broker entities are provisioned out of band (an
    /// infrastructure-as-code deployment, or an identity that may send and receive but not manage),
    /// and the transport then assumes every entity exists.
    /// </para>
    /// </summary>
    public bool ProvisionTopology { get; init; } = true;

    /// <summary>
    /// Gets the number of in-process retries the receive pipeline makes after a handler failure,
    /// each in a fresh DI scope with an exponential delay, before the message is handed to
    /// second-level redelivery (or dead-lettered when none is left). A message is therefore attempted
    /// up to <c>RetryLimit + 1</c> times per delivery. Set to <c>0</c> to disable in-process retries.
    /// Defaults to <c>5</c>.
    /// </summary>
    [Range(0, 20)]
    public int RetryLimit { get; init; } = 5;

    /// <summary>
    /// Gets the first retry interval, in seconds. Subsequent intervals double, up to
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
    /// <see cref="Provider"/>: ON for any broker transport, OFF for
    /// <see cref="MessageBusProviders.InProcess"/>, which has no redelivery to dedup. Broker
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
    /// for <see cref="MessageBusProviders.InProcess"/>. Every framework component reads this rather
    /// than the raw setting.
    /// </summary>
    public bool IsInboxEnabled => EnableInbox ?? !MessageBusProviders.IsInProcess(Provider);

    /// <summary>
    /// Gets the explicit override for the transactional outbox, or <see langword="null"/> (the
    /// default) to let the selected transport decide. When enabled, domain and integration events
    /// are written to the <c>OutboxMessages</c> table in the same transaction as the aggregate
    /// changes and delivered by <c>OutboxProcessor</c>; when disabled, the in-process buses dispatch
    /// events synchronously and no outbox rows are written.
    /// <para>
    /// Leave it unset and <see cref="IsOutboxEnabled"/> resolves it from <see cref="Provider"/>: ON
    /// for a broker transport, OFF for
    /// <see cref="MessageBusProviders.InProcess"/>. A single-process application dispatches every
    /// event inside the same process that raised it, so the store-and-forward hop buys it nothing
    /// but two background services, a table and a poll loop; a broker deployment cannot deliver at
    /// all without it, because the only correct publish path crosses a process boundary.
    /// </para>
    /// <para>
    /// An explicit value always wins for the in-process transport, in both directions: a monolith
    /// that wants at-least-once delivery across a crash sets <c>MessageBus:EnableOutbox=true</c> and
    /// gets the full outbox back. Turning it OFF under a broker is refused at registration rather
    /// than honored: a broker with no outbox has no delivery channel at all, so the misconfiguration
    /// is a startup failure instead of silently dropped events. The <c>OutboxMessages</c> table stays
    /// part of the EF model either way, so switching the flag never needs a migration.
    /// </para>
    /// <para>
    /// With the outbox off a handler failure is logged at Error and nothing retries it; each event
    /// dispatches on its own, so one failing handler does not stop the later events of the save.
    /// Handlers that need retry need the outbox.
    /// </para>
    /// </summary>
    public bool? EnableOutbox { get; init; }

    /// <summary>
    /// Gets the RESOLVED outbox posture: the explicit <see cref="EnableOutbox"/> value when the host
    /// set one, otherwise <see langword="true"/> for a broker transport and <see langword="false"/>
    /// for <see cref="MessageBusProviders.InProcess"/>. Every framework component reads this rather
    /// than the raw setting.
    /// </summary>
    public bool IsOutboxEnabled => EnableOutbox ?? !MessageBusProviders.IsInProcess(Provider);

    /// <summary>
    /// Gets the framework's default second-level redelivery intervals, in seconds:
    /// <c>[60, 600, 3600]</c> (one minute, ten minutes, one hour), a spread wide enough to ride out
    /// a dependency restart, a failover and a short incident without an operator replaying the
    /// dead-letter queue by hand. The receive pipeline applies it when
    /// <see cref="RedeliveryIntervalsSeconds"/> is not configured, clamping each step to the
    /// transport's <see cref="IBrokerTransport.MaxRedeliveryDelay"/> when it declares one.
    /// </summary>
    public static IReadOnlyList<int> DefaultRedeliveryIntervalsSeconds { get; } = [60, 600, 3600];

    /// <summary>
    /// Gets the configured second-level redelivery intervals, in seconds, or <see langword="null"/>
    /// (the default) to use <see cref="DefaultRedeliveryIntervalsSeconds"/>. Each entry is one
    /// redelivery the receive pipeline schedules on the host's queue after the in-process retries
    /// are exhausted; the message dead-letters only after the last interval also fails. A configured
    /// list REPLACES the default; it is never merged with it. Non-positive entries are dropped, so
    /// <c>[0]</c> turns second-level redelivery off (an empty array cannot be expressed in
    /// configuration). An interval longer than the transport's
    /// <see cref="IBrokerTransport.MaxRedeliveryDelay"/> is shortened to it (Amazon SQS caps a
    /// message delay at 15 minutes).
    /// <para>
    /// The property has no initializer on purpose: the configuration binder appends a configured
    /// list to an existing collection instead of replacing it, so a non-null default would make the
    /// defaults impossible to shorten by configuration.
    /// </para>
    /// <para>
    /// Every transport applies it: RabbitMQ through per-interval delay queues (no plugin), PostgreSQL
    /// through the row's visibility time, Azure Service Bus and Amazon SQS through their native
    /// scheduling.
    /// </para>
    /// </summary>
    public IReadOnlyList<int>? RedeliveryIntervalsSeconds { get; init; }

    /// <summary>
    /// Gets the number of messages the transport may hold locally ahead of the consumer, or
    /// <see langword="null"/> (the default) to leave the transport's own default in place. A prefetch
    /// window larger than the work a consumer can finish inside the lock or lease is what turns a slow
    /// consumer into redeliveries; a window of one serializes the queue. Values of zero or less are
    /// ignored rather than pushed to the transport, because the binder never validates this section
    /// (see <c>AddBrokerMessaging</c>), so the guard has to live in code.
    /// </summary>
    public int? PrefetchCount { get; init; }

    /// <summary>
    /// Gets the maximum number of messages consumed concurrently from the host's queue, or
    /// <see langword="null"/> (the default) to leave the transport's own default in place. This is the
    /// backpressure knob: it caps how much work the service takes on at once, and therefore how many
    /// database connections and downstream calls one burst of events can open. Values of zero or less
    /// are ignored, same as <see cref="PrefetchCount"/>.
    /// </summary>
    public int? ConcurrentMessageLimit { get; init; }
}
