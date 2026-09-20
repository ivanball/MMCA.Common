using System.Diagnostics.CodeAnalysis;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Infrastructure.Http;
using MMCA.Common.Infrastructure.Messaging;

namespace MMCA.Common.Infrastructure;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Replaces the default in-process <see cref="IMessageBus"/> registration with a
        /// MassTransit-backed <see cref="BrokerMessageBus"/>. Call this from a microservice
        /// host's <c>Program.cs</c> AFTER <c>AddInfrastructure(configuration)</c>.
        /// <para>
        /// The transport is selected by <see cref="MessageBusSettings.Provider"/>:
        /// <list type="bullet">
        ///   <item><see cref="MessageBusProvider.RabbitMq"/> — local dev (Aspire RabbitMQ container).</item>
        ///   <item><see cref="MessageBusProvider.AzureServiceBus"/> — production deployments.</item>
        ///   <item><see cref="MessageBusProvider.InProcess"/> — no-op; this method returns without
        ///   modifying the container, leaving the default <see cref="InProcessMessageBus"/> in place.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Consumer registration is the responsibility of each service: pass an action that
        /// calls <c>x.AddConsumer&lt;TConsumer&gt;()</c> for every <c>IIntegrationEventHandler&lt;T&gt;</c>
        /// or <c>IConsumer&lt;T&gt;</c> implementation in the service.
        /// </para>
        /// </summary>
        /// <param name="configuration">Application configuration providing the <c>MessageBus</c> section.</param>
        /// <param name="configureConsumers">Optional callback for registering MassTransit consumers.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddBrokerMessaging(
            IConfiguration configuration,
            Action<IBusRegistrationConfigurator>? configureConsumers = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var settings = configuration.GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()
                ?? new MessageBusSettings();

            if (settings.Provider == MessageBusProvider.InProcess)
            {
                return services;
            }

            // Checked here as well as in AddInfrastructure: a service host that wires the broker
            // without the full infrastructure registration must still fail loudly rather than run a
            // broker bus whose only delivery channel was turned off.
            EnsureOutboxAvailableForProvider(settings);

            var connectionString = ResolveBrokerConnectionString(configuration, settings);

            services.AddMassTransit(x =>
            {
                // The prefix is the whole point: the formatter has to carry it, or every service on a
                // shared broker derives the same kebab-case queue name from the same consumer type
                // and they collide. includeNamespace: false keeps the name to the consumer's short
                // type name, so the prefix is the only namespacing applied.
                //
                // SECURITY (SEC-Common-53): unset now means "the application namespace", not "no
                // prefix". An omitted setting used to put two applications' consumers on the same
                // queue names on a shared broker, so one application's messages were delivered to
                // the other's consumers.
                //
                // PreserveDefaultEndpointNames keeps the pre-1.188.0 names (MassTransit's default
                // formatter, no prefix) so an existing deployment does not rename its queues and
                // subscriptions at cutover; it is the documented opt-out, not the default.
                if (!settings.PreserveDefaultEndpointNames)
                {
                    var endpointPrefix = string.IsNullOrWhiteSpace(settings.EndpointPrefix)
                        ? ApplicationNamespace.Resolve(configuration, environment: null)
                        : settings.EndpointPrefix;

                    x.SetEndpointNameFormatter(
                        new KebabCaseEndpointNameFormatter(endpointPrefix, includeNamespace: false));
                }

                configureConsumers?.Invoke(x);
                ConfigureBrokerTransport(x, settings, connectionString);
            });

            // Swap the default in-process bus for the broker-backed one. Use Replace so we
            // overwrite the AddServices() registration rather than appending a second one.
            services.Replace(ServiceDescriptor.Scoped<IMessageBus, BrokerMessageBus>());

            // Also replace IEventBus so application code that publishes integration events
            // (via IEventBus.PublishAsync) writes to the outbox
            // and signals the OutboxProcessor — but does NOT dispatch in-process. The
            // OutboxProcessor's broker-publish path becomes the only delivery channel.
            services.Replace(ServiceDescriptor.Scoped<IEventBus, BrokerEventBus>());

            // Consumer-side idempotency. This branch only runs for a broker transport (the
            // in-process provider returned above), and MessageBusSettings.IsInboxEnabled resolves
            // unset to ON for a broker: at-least-once delivery without dedup is not a default worth
            // shipping. A host that has not migrated the InboxMessages table opts out explicitly
            // with MessageBus:EnableInbox=false and gets the startup Warning below.
            if (settings.IsInboxEnabled)
            {
                services.TryAddScoped<Persistence.Inbox.IInboxStore, Persistence.Inbox.EfInboxStore>();
            }
            else
            {
                services.TryAddSingleton<Persistence.Inbox.IInboxStore, Persistence.Inbox.NoOpInboxStore>();

                // Loudly off: a disabled dedup store looks exactly like an enabled one until a
                // duplicate side effect reaches a customer. One startup Warning makes the posture
                // visible for the cost of a single log line.
                services.AddHostedService<Persistence.Inbox.InboxDisabledWarningService>();
            }

            return services;
        }

        /// <summary>
        /// Registers a typed service client (<typeparamref name="TInterface"/> →
        /// <typeparamref name="TImplementation"/>) backed by an <see cref="HttpClient"/> wired
        /// to Aspire service discovery (<c>http://{serviceName}</c>), Polly resilience (matching
        /// the standard handler from <c>AddServiceDefaults</c>), and the
        /// <see cref="JwtForwardingDelegatingHandler"/> for forwarding the inbound JWT bearer
        /// token to the downstream service.
        /// <para>
        /// Use this for HTTP-based cross-service contracts that don't warrant a gRPC binding —
        /// e.g., webhook receivers, public REST endpoints, or third-party API wrappers.
        /// gRPC is preferred for service-to-service contracts; see
        /// <c>MMCA.Common.Grpc.AddTypedGrpcClient&lt;T&gt;</c>.
        /// </para>
        /// </summary>
        /// <typeparam name="TInterface">The contract interface that consumer code depends on.</typeparam>
        /// <typeparam name="TImplementation">The class implementing the interface, taking <see cref="HttpClient"/> in its constructor.</typeparam>
        /// <param name="serviceName">The Aspire service-discovery name (e.g. <c>"identity"</c>).</param>
        /// <returns>The <see cref="IHttpClientBuilder"/> for further customization.</returns>
        public IHttpClientBuilder AddTypedServiceClient<TInterface, TImplementation>(string serviceName)
            where TInterface : class
            where TImplementation : class, TInterface
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

            services.AddHttpContextAccessor();
            services.TryAddTransient<JwtForwardingDelegatingHandler>();

#pragma warning disable S5332 // Deliberate h2c-style cleartext service-discovery address for in-cluster HTTP calls, same design as MMCA.Common.Grpc.AddTypedGrpcClient
            var builder = services.AddHttpClient<TInterface, TImplementation>(client =>
                    client.BaseAddress = new Uri($"http://{serviceName}"))
                .AddHttpMessageHandler<JwtForwardingDelegatingHandler>();
#pragma warning restore S5332

            builder.AddStandardResilienceHandler();
            return builder;
        }
    }

    /// <summary>
    /// Fails the registration when a broker transport is paired with an explicitly disabled outbox.
    /// The outbox is the ONLY publish path a broker deployment has: <c>BrokerEventBus</c> writes rows
    /// and signals, and <c>OutboxProcessor</c> is what turns them into broker messages. Without the
    /// processor every published integration event would accumulate unsent (or, with the rows also
    /// skipped, vanish), which looks exactly like a healthy service until a downstream consumer is
    /// found to have received nothing for hours. Throwing here makes it a startup failure the first
    /// time the host runs instead of a silent loss of every cross-service event.
    /// </summary>
    /// <param name="settings">The resolved message bus settings.</param>
    /// <exception cref="InvalidOperationException">
    /// A broker transport is configured and <c>MessageBus:EnableOutbox</c> is explicitly
    /// <see langword="false"/>.
    /// </exception>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddInfrastructure and AddBrokerMessaging inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static void EnsureOutboxAvailableForProvider(MessageBusSettings settings)
    {
        if (settings.Provider != MessageBusProvider.InProcess && settings.EnableOutbox == false)
        {
            throw new InvalidOperationException(
                $"MessageBus:EnableOutbox=false is not supported with the '{settings.Provider}' transport. A broker deployment publishes integration events exclusively through the outbox (BrokerEventBus writes the rows, OutboxProcessor publishes them), so disabling it drops every cross-service event silently. Remove MessageBus:EnableOutbox (or set it to true) for a broker transport; the setting exists to let a single-process host opt out of store-and-forward.");
        }
    }

    /// <summary>
    /// Resolves the broker connection string. Order of precedence:
    /// <list type="number">
    ///   <item><c>MessageBus:ConnectionString</c> — explicit override in appsettings/secrets.</item>
    ///   <item><c>ConnectionStrings:rabbitmq</c> — Aspire injects this via <c>WithReference(broker)</c>.</item>
    ///   <item><c>ConnectionStrings:messaging</c> — alternative Aspire convention.</item>
    /// </list>
    /// Without this fallback, MassTransit defaults to <c>localhost:5672</c> and fails to reach
    /// the Aspire-allocated broker container port.
    /// </summary>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddBrokerMessaging inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static string? ResolveBrokerConnectionString(IConfiguration configuration, MessageBusSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return settings.ConnectionString;
        }

        return configuration.GetConnectionString("rabbitmq")
            ?? configuration.GetConnectionString("messaging");
    }

    /// <summary>
    /// Wires MassTransit to the configured broker transport using the resolved connection string.
    /// Every receive endpoint gets an exponential-backoff <c>UseMessageRetry</c> policy (configured
    /// by <see cref="MessageBusSettings.RetryLimit"/> and friends) so a transient handler failure is
    /// retried in-process instead of dead-lettering on the first exception.
    /// Extracted out of <c>AddBrokerMessaging</c> to keep that method's cyclomatic complexity
    /// below the analyzer threshold.
    /// </summary>
    /// <remarks>
    /// Second-level redelivery (<c>UseDelayedRedelivery</c>) sits ABOVE the in-process retry policy:
    /// in-process retry absorbs a blip measured in seconds, delayed redelivery reschedules the
    /// message through the broker over <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/>
    /// (one minute, ten minutes, one hour by default) so an outage measured in minutes or hours
    /// does not dead-letter the event. It is registered before <c>UseMessageRetry</c> so the retry
    /// filter runs innermost: every immediate attempt is exhausted before a redelivery is scheduled.
    /// <para>
    /// The two transports differ in posture. Azure Service Bus schedules messages natively, so
    /// redelivery is applied UNCONDITIONALLY there. RabbitMQ needs the
    /// <c>rabbitmq_delayed_message_exchange</c> plugin, which the Aspire development container does
    /// not ship, so it is gated behind <see cref="MessageBusSettings.EnableDelayedRedelivery"/>
    /// (default <see langword="false"/>) and must only be turned on against a broker that has the
    /// plugin installed.
    /// </para>
    /// </remarks>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddBrokerMessaging inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static void ConfigureBrokerTransport(
        IBusRegistrationConfigurator x,
        MessageBusSettings settings,
        string? connectionString)
    {
        switch (settings.Provider)
        {
            case MessageBusProvider.RabbitMq:
                x.UsingRabbitMq((context, cfg) =>
                {
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        cfg.Host(new Uri(connectionString));
                    }

                    // Opt-in: needs the rabbitmq_delayed_message_exchange plugin, which the Aspire
                    // development container does not ship. Registered before UseMessageRetry so the
                    // retry filter stays innermost (all immediate attempts first, then a scheduled
                    // redelivery).
                    if (settings.EnableDelayedRedelivery)
                    {
                        TimeSpan[] intervals = BuildRedeliveryIntervals(settings);
                        if (intervals.Length > 0)
                        {
                            cfg.UseDelayedRedelivery(r => r.Intervals(intervals));
                        }
                    }

                    cfg.UseMessageRetry(r => r.Exponential(
                        settings.RetryLimit,
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMaxIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds)));
                    ApplyBackpressure(cfg, settings);
                    cfg.ConfigureEndpoints(context);
                });
                break;

            case MessageBusProvider.AzureServiceBus:
                x.UsingAzureServiceBus((context, cfg) =>
                {
                    if (!string.IsNullOrWhiteSpace(connectionString))
                    {
                        // The emulator branch exists so a development stack can run the SAME
                        // transport production runs. It is entered only when the connection string
                        // carries UseDevelopmentEmulator=true, a token no real namespace emits, so
                        // the production path below is reached byte-for-byte as before.
                        if (Messaging.ServiceBusEmulatorSupport.IsEmulatorConnectionString(connectionString))
                        {
                            Messaging.ServiceBusEmulatorSupport.ConfigureEmulatorHost(
                                cfg, connectionString, settings.EmulatorAdminEndpoint);
                        }
                        else
                        {
                            cfg.Host(connectionString);
                        }
                    }

                    // Unconditional: Azure Service Bus schedules messages natively, so there is no
                    // plugin to install and no configuration in which this can fail at bus start.
                    // The EnableDelayedRedelivery flag is deliberately not consulted on this
                    // transport, because it exists only to gate the RabbitMQ plugin requirement.
                    TimeSpan[] intervals = BuildRedeliveryIntervals(settings);
                    if (intervals.Length > 0)
                    {
                        cfg.UseDelayedRedelivery(r => r.Intervals(intervals));
                    }

                    cfg.UseMessageRetry(r => r.Exponential(
                        settings.RetryLimit,
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMaxIntervalSeconds),
                        TimeSpan.FromSeconds(settings.RetryMinIntervalSeconds)));
                    ApplyBackpressure(cfg, settings);
                    cfg.ConfigureEndpoints(context);
                });
                break;

            case MessageBusProvider.InProcess:
            default:
                // Caller short-circuits InProcess before reaching this method.
                break;
        }
    }

    /// <summary>
    /// Applies the two backpressure knobs to the bus factory configurator, which is where MassTransit
    /// carries them onto every receive endpoint <c>ConfigureEndpoints</c> then builds. Both are
    /// optional and both are range-guarded HERE rather than with a <c>[Range]</c> attribute:
    /// <c>AddBrokerMessaging</c> binds this section with <c>Get&lt;MessageBusSettings&gt;()</c>, which
    /// never runs DataAnnotations, so an annotation would document the bound without enforcing it. A
    /// value of zero or less is left unapplied, so the transport keeps its own default instead of
    /// receiving a window that would stall the endpoint.
    /// </summary>
    /// <param name="cfg">The transport's bus factory configurator.</param>
    /// <param name="settings">The bound message-bus settings.</param>
    private static void ApplyBackpressure(IBusFactoryConfigurator cfg, MessageBusSettings settings)
    {
        if (settings.PrefetchCount is > 0)
        {
            cfg.PrefetchCount = settings.PrefetchCount.Value;
        }

        if (settings.ConcurrentMessageLimit is > 0)
        {
            cfg.ConcurrentMessageLimit = settings.ConcurrentMessageLimit.Value;
        }
    }

    /// <summary>
    /// Maps <see cref="MessageBusSettings.RedeliveryIntervalsSeconds"/> to the
    /// <see cref="TimeSpan"/> array MassTransit's redelivery configurator expects. Non-positive
    /// entries are dropped: a zero or negative interval schedules an immediate redelivery, which
    /// is what <c>UseMessageRetry</c> already does and would turn the second level into a hot loop.
    /// Returns an empty array when nothing survives, and the caller then skips the filter entirely
    /// rather than registering a redelivery policy with no attempts.
    /// </summary>
    private static TimeSpan[] BuildRedeliveryIntervals(MessageBusSettings settings) =>
        [.. (settings.RedeliveryIntervalsSeconds ?? [])
            .Where(seconds => seconds > 0)
            .Select(seconds => TimeSpan.FromSeconds(seconds))];
}
