using System.Diagnostics.CodeAnalysis;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;

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
        ///   <item><see cref="MessageBusProviders.RabbitMq"/> — built in; local dev (Aspire RabbitMQ container).</item>
        ///   <item><see cref="MessageBusProviders.InProcess"/> — no-op; this method returns without
        ///   modifying the container, leaving the default <see cref="InProcessMessageBus"/> in place.</item>
        ///   <item>Any other name — the <see cref="IBrokerTransport"/> an adapter package registered
        ///   under it, e.g. <c>AzureServiceBus</c> through <c>AddAzureServiceBusTransport()</c> from
        ///   <c>MMCA.Common.Azure</c>. The adapter call must come BEFORE this one; a name with no
        ///   registered transport throws here, naming the package and the call.</item>
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

            if (MessageBusProviders.IsInProcess(settings.Provider))
            {
                return services;
            }

            // Checked here as well as in AddInfrastructure: a service host that wires the broker
            // without the full infrastructure registration must still fail loudly rather than run a
            // broker bus whose only delivery channel was turned off.
            EnsureOutboxAvailableForProvider(settings);

            // Resolved before AddMassTransit so an unregistered provider fails without leaving a
            // half-registered bus behind.
            var transport = ResolveBrokerTransport(services, settings.Provider);
            var transportContext = new BrokerTransportContext(
                settings,
                ResolveBrokerConnectionString(configuration, settings),
                configuration);

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
                transport.Configure(x, transportContext);
            });

            // Swap the default in-process bus for the broker-backed one. Use Replace so we
            // overwrite the AddServices() registration rather than appending a second one.
            services.Replace(ServiceDescriptor.Scoped<IMessageBus, BrokerMessageBus>());

            // Also replace IEventBus so application code that publishes integration events
            // (via IEventBus.PublishAsync) writes to the outbox
            // and signals the OutboxProcessor, but does NOT dispatch in-process. The
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

                // The store stamps ProcessedOn from the injected clock. AddServices registers it
                // too; TryAdd keeps this call self-sufficient without displacing a host's own clock.
                services.TryAddSingleton(TimeProvider.System);
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
        /// Registers a broker transport that <see cref="AddBrokerMessaging"/> can select by name
        /// through <c>MessageBus:Provider</c>. Adapter packages call this from their own registration
        /// method (for example <c>AddAzureServiceBusTransport()</c>); a host calls it directly only
        /// for a transport of its own. Must run BEFORE <see cref="AddBrokerMessaging"/>, which picks
        /// the transport while it registers the bus. Idempotent per transport type, so registering
        /// the same adapter twice is harmless; when two transports share a name, the last one
        /// registered wins.
        /// </summary>
        /// <param name="transport">The transport instance.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddBrokerTransport(IBrokerTransport transport)
        {
            ArgumentNullException.ThrowIfNull(transport);

            if (string.IsNullOrWhiteSpace(transport.Name))
            {
                throw new ArgumentException("A broker transport must have a non-empty Name.", nameof(transport));
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IBrokerTransport>(transport));

            return services;
        }
    }

    /// <summary>
    /// Picks the transport for a broker provider: the built-in RabbitMQ transport, else the LAST
    /// adapter transport registered under that name (instances only, read straight off the
    /// collection because the choice has to be made while the bus is being registered, before any
    /// provider exists).
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="provider">The configured <c>MessageBus:Provider</c>.</param>
    /// <returns>The transport to configure the bus with.</returns>
    /// <exception cref="InvalidOperationException">No transport is registered under <paramref name="provider"/>.</exception>
    [SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Called from AddBrokerMessaging inside the extension(IServiceCollection services) block above. The IDE0051 analyzer in .NET SDK 10.0.201+ does not see references that cross the boundary between a C# preview extension type block and outer-scope private members of the same containing class, so it reports a false positive. The local SDK 10.0.104 analyzer correctly resolves the call. Remove this suppression once Roslyn fixes the cross-block reference tracking.")]
    private static IBrokerTransport ResolveBrokerTransport(IServiceCollection services, string provider)
    {
        if (MessageBusProviders.IsRabbitMq(provider))
        {
            return new RabbitMqBrokerTransport();
        }

        var registered = services
            .Where(descriptor => descriptor.ServiceType == typeof(IBrokerTransport) && !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IBrokerTransport>()
            .ToList();

        var match = registered.LastOrDefault(t => string.Equals(t.Name, provider, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var available = string.Join(
            ", ",
            new[] { MessageBusProviders.InProcess, MessageBusProviders.RabbitMq }.Concat(registered.Select(t => t.Name)));
        var remedy = MessageBusProviders.KnownAdapterTransports.TryGetValue(provider, out var adapter)
            ? $"Reference the {adapter.Package} package and call {adapter.Registration} BEFORE AddBrokerMessaging(...)."
            : $"Register an IBrokerTransport named '{provider}' with services.AddBrokerTransport(...) BEFORE AddBrokerMessaging(...), or correct the provider name.";

        throw new InvalidOperationException(
            $"MessageBus:Provider is '{provider}', but no broker transport by that name is registered. {remedy} Available providers: {available}.");
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
        if (!MessageBusProviders.IsInProcess(settings.Provider) && settings.EnableOutbox == false)
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
}
