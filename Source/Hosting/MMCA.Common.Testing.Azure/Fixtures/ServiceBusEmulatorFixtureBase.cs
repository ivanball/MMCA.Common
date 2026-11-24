using System.Collections.Concurrent;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Azure;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging.Broker;
using Testcontainers.ServiceBus;
using Xunit;

namespace MMCA.Common.Testing.Azure.Fixtures;

/// <summary>
/// Shared scaffolding for the Azure Service Bus emulator <b>broker-parity</b> test tier: a collection
/// fixture that starts ONE warm emulator (plus its companion SQL Server container, provisioned
/// automatically by <c>Testcontainers.ServiceBus</c>) for the whole tier and, optionally, runs the
/// framework's REAL Azure Service Bus transport against it: a host built with
/// <c>AddAzureServiceBusTransport()</c> and <c>AddBrokerMessaging</c>, publishing through the
/// framework's <see cref="IMessageBus"/> and receiving through its receive pipeline. One shared
/// container respects the emulator's 10-connection namespace quota and its roughly
/// one-admin-operation-per-second throttle: the transport provisions its topology through the admin
/// plane, so serial tests against one warm emulator are the stable shape.
/// <para>
/// The image is pinned (<see cref="DefaultEmulatorImage"/>): the admin plane
/// (<see cref="ServiceBusAdministrationClient"/> over HTTP port 5300) shipped in emulator 2.0.0 and is
/// what lets the transport create its own topics, subscriptions and queue, so a silent downgrade to a
/// 1.x image would break the tier's whole premise.
/// </para>
/// <para>
/// <b>The host lives on the FIXTURE, never on the test class.</b> A test class implementing
/// <see cref="IAsyncLifetime"/> is re-instantiated per <c>[Fact]</c>, so a host created there starts once
/// PER TEST and every start re-provisions the topology through that throttled admin plane (MMCA.ADC's
/// job hung and was killed at its timeout on 7 of 7 scheduled runs, 2026-07-21 to 2026-07-24, in that
/// shape). Hoisting the host here makes it one provisioning cycle per run, and the transport's one
/// queue per host means every extra contract costs a topic and a subscription, not another queue.
/// </para>
/// <para>
/// Both startup phases are wall-clock bounded. The point is not only to fail sooner: a step killed by
/// the JOB timeout has its output discarded, so a hang leaves no evidence of WHICH phase hung. A bounded
/// phase throws a named <see cref="TimeoutException"/> instead, the step completes, and its log survives.
/// <c>WaitAsync</c> rather than a <see cref="CancellationToken"/>, because the observed hang does not
/// honour cancellation; bounding the await is what makes the limit enforceable.
/// </para>
/// <para>
/// Stays in the subclass: the sealed fixture itself, its <c>[CollectionDefinition]</c> class (a
/// collection definition is per test assembly by construction), the integration-event contracts
/// (<see cref="ConfigureConsumers"/>, usually one <see cref="RecordConsumed{TEvent}"/> per contract),
/// and the assertions.
/// </para>
/// </summary>
public abstract class ServiceBusEmulatorFixtureBase : IAsyncLifetime
{
    /// <summary>
    /// The pinned emulator image. 2.x on purpose: the HTTP management plane the transport provisions
    /// its topology through shipped in 2.0.0, so a 1.x image would leave the broker unusable rather
    /// than merely older.
    /// </summary>
    public const string DefaultEmulatorImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1";

    /// <summary>The emulator's HTTP management-plane container port.</summary>
    public const int AdminPlanePort = 5300;

    private ServiceBusContainer? _container;
    private ServiceBusClient? _client;
    private string? _connectionString;
    private IHost? TransportHost { get; set; }

    /// <summary>AMQP (data-plane) client bound to the running emulator.</summary>
    public ServiceBusClient Client =>
        _client ?? throw new InvalidOperationException("The Service Bus emulator has not started yet.");

    /// <summary>
    /// Admin (management-plane) client bound to the emulator's mapped HTTP 5300 endpoint. Assigned in
    /// <see cref="InitializeAsync"/>, so it is only meaningful once the fixture has started.
    /// </summary>
    public ServiceBusAdministrationClient AdminClient { get; private set; } = null!;

    /// <summary>
    /// The emulator's AMQP connection string (it carries <c>UseDevelopmentEmulator=true</c>), for a
    /// test that wires a host of its own as <c>MessageBus:ConnectionString</c>.
    /// </summary>
    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The Service Bus emulator has not started yet.");

    /// <summary>
    /// The emulator's management-plane base address (<c>http://host:port</c>), for a test that wires a
    /// host of its own as <c>MessageBus:EmulatorAdminEndpoint</c>.
    /// </summary>
    public string AdminEndpoint
    {
        get => field ?? throw new InvalidOperationException("The Service Bus emulator has not started yet.");
        private set;
    }

    /// <summary>
    /// The services of the one transport host for the whole tier, started once with its topology
    /// provisioned. Only available when <see cref="ReceiveQueueName"/> names a queue; a fixture that
    /// wants the clients alone never touches it.
    /// </summary>
    public IServiceProvider Services =>
        TransportHost?.Services ?? throw new InvalidOperationException(
            "No transport host was started. Override ReceiveQueueName to have this fixture host one.");

    /// <summary>
    /// Everything the host consumed this run, added by the handlers <see cref="RecordConsumed{TEvent}"/>
    /// registers. Shared across the tier because the host is: each test matches on a value unique to
    /// itself, so one bag cannot cross-contaminate.
    /// </summary>
    public ConcurrentBag<object> Consumed { get; } = [];

    /// <summary>
    /// The emulator image, overridable only to pin a different 2.x build (or, in a hardening test, to
    /// point at a tag that cannot start and prove the PHASE 1 timeout fires).
    /// </summary>
    protected virtual string EmulatorImage => DefaultEmulatorImage;

    /// <summary>
    /// Budget for PHASE 1, the emulator container and its companion SQL image coming up. Generous
    /// because it covers a cold image pull on a CI runner.
    /// </summary>
    protected virtual TimeSpan ContainerStartTimeout => TimeSpan.FromMinutes(4);

    /// <summary>
    /// Budget for PHASE 2, the transport host starting and its topology (the queue, a topic and a
    /// subscription per consumed contract) existing on the emulator.
    /// </summary>
    protected virtual TimeSpan BusStartTimeout => TimeSpan.FromMinutes(3);

    /// <summary>Budget for stopping the transport host on teardown, after which the container is torn down anyway.</summary>
    protected virtual TimeSpan BusStopTimeout => TimeSpan.FromMinutes(1);

    /// <summary>
    /// The host's queue (<c>MessageBus:QueueName</c>) every contract in the tier is bound to, or
    /// <see langword="null"/> (the default) for a fixture that wants only <see cref="Client"/> and
    /// <see cref="AdminClient"/> and no transport host at all. Also used as the application namespace
    /// topics are prefixed with, so two tiers on one emulator never share a topic.
    /// </summary>
    protected virtual string? ReceiveQueueName => null;

    /// <summary>
    /// Builds the emulator's management-plane connection string. Pure and static so a fixture's
    /// connection-string composition is unit-testable without a container.
    /// <para>
    /// The container module's own connection string targets the mapped AMQP port, so the admin plane
    /// needs its own against the mapped 5300 port. <c>UseDevelopmentEmulator</c> keeps both clients on
    /// plain TCP/HTTP against localhost.
    /// </para>
    /// </summary>
    /// <param name="hostname">The container host name.</param>
    /// <param name="mappedAdminPort">The host port <see cref="AdminPlanePort"/> is published on.</param>
    /// <returns>The admin-plane connection string.</returns>
    public static string ComposeAdminConnectionString(string hostname, int mappedAdminPort) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Endpoint=sb://{hostname}:{mappedAdminPort};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    /// <summary>
    /// The <c>MessageBus</c> configuration the transport host runs with: the Azure Service Bus provider
    /// on the emulator, the receive queue, the inbox off (the tier has no database; the transport, not
    /// the inbox, is under test) and the outbox left to the host. Pure and static so the composition is
    /// unit-testable without a container.
    /// </summary>
    /// <param name="connectionString">The emulator AMQP connection string.</param>
    /// <param name="adminEndpoint">The emulator management-plane base address.</param>
    /// <param name="queueName">The receive queue.</param>
    /// <returns>The configuration entries.</returns>
    public static Dictionary<string, string?> ComposeHostConfiguration(string connectionString, string adminEndpoint, string queueName) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["applicationName"] = queueName,
            ["Application:Namespace"] = queueName,
            ["MessageBus:Provider"] = "AzureServiceBus",
            ["MessageBus:ConnectionString"] = connectionString,
            ["MessageBus:EmulatorAdminEndpoint"] = adminEndpoint,
            ["MessageBus:QueueName"] = queueName,
            ["MessageBus:EnableInbox"] = "false",
        };

    /// <summary>
    /// Publishes <paramref name="integrationEvent"/> through the host's <see cref="IMessageBus"/>, the
    /// same path the outbox processor drains through in production.
    /// </summary>
    /// <param name="integrationEvent">The event to publish.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>A task that completes when the emulator accepted the message.</returns>
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var scope = Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .PublishAsync(integrationEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Built here rather than in a field initializer so a subclass can be constructed (and its
        // non-container logic unit-tested) on a machine with no Docker daemon, and so EmulatorImage is
        // read as a virtual member rather than during base construction.
        _container = new ServiceBusBuilder(EmulatorImage)
            .WithAcceptLicenseAgreement(true)
            .Build();

        try
        {
            await _container.StartAsync().WaitAsync(ContainerStartTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"PHASE 1 (container): the Service Bus emulator container ({EmulatorImage}) and its companion " +
                $"SQL image did not start within {ContainerStartTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes. This is the " +
                "companion-SQL startup hang, NOT admin-plane provisioning: the emulator image itself is pinned, " +
                "so suspect the floating companion SQL tag.",
                ex);
        }

        var mappedAdminPort = _container.GetMappedPublicPort(AdminPlanePort);
        _connectionString = _container.GetConnectionString();
        AdminEndpoint = $"http://{_container.Hostname}:{mappedAdminPort}";
        _client = new ServiceBusClient(_connectionString);
        AdminClient = new ServiceBusAdministrationClient(ComposeAdminConnectionString(_container.Hostname, mappedAdminPort));

        var queueName = ReceiveQueueName;
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return;
        }

        try
        {
            await StartHostAsync(queueName).WaitAsync(BusStartTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"PHASE 2 (topology): the container started, but the transport host did not finish provisioning the " +
                $"topology within {BusStartTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes. The container is therefore NOT " +
                "the culprit; the admin plane is. Provisioning is already down to one cycle per run, so the next " +
                "lever is fewer entities (one contract) or an explicit pre-provision before the host starts.",
                ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        // Best effort, in reverse order of creation. A failure here must not mask a real test failure,
        // and on either InitializeAsync timeout path the later members were never assigned.
        if (TransportHost is not null)
        {
            try
            {
                await TransportHost.StopAsync().WaitAsync(BusStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The container is torn down next, which takes the whole namespace with it.
            }

            // Asynchronously: the transport's clients are async-disposable only.
            if (TransportHost is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                TransportHost.Dispose();
            }
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Declares the tier's contracts: usually one <see cref="RecordConsumed{TEvent}"/> per contract,
    /// each adding its message to <see cref="Consumed"/>, or a <c>RegisterIntegrationEventConsumer</c>
    /// plus handlers of the subclass's own. Defaults to none, which provisions nothing and runs no
    /// receiver (the host can still publish).
    /// </summary>
    /// <param name="consumers">The broker consumer builder of the host.</param>
    /// <param name="services">The host's services, for handlers.</param>
    protected virtual void ConfigureConsumers(IBrokerConsumerBuilder consumers, IServiceCollection services)
    {
    }

    /// <summary>
    /// Adjusts the host's configuration before it is built, for example the retry and redelivery
    /// settings under <c>MessageBus</c>. Defaults to <see cref="ComposeHostConfiguration"/> unchanged.
    /// </summary>
    /// <param name="configuration">The configuration entries, editable in place.</param>
    protected virtual void ConfigureHost(IDictionary<string, string?> configuration)
    {
    }

    /// <summary>
    /// Subscribes the host to <typeparamref name="TEvent"/> and registers a handler that adds every
    /// delivered message to <see cref="Consumed"/>.
    /// </summary>
    /// <typeparam name="TEvent">The integration event contract.</typeparam>
    /// <param name="consumers">The broker consumer builder passed to <see cref="ConfigureConsumers"/>.</param>
    /// <param name="services">The services passed to <see cref="ConfigureConsumers"/>.</param>
    protected void RecordConsumed<TEvent>(IBrokerConsumerBuilder consumers, IServiceCollection services)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(services);

        consumers.RegisterIntegrationEventConsumer<TEvent>();
        services.AddSingleton<IIntegrationEventHandler<TEvent>>(new RecordingHandler<TEvent>(Consumed));
    }

    /// <summary>
    /// Builds and starts the transport host, then waits until the queue and every consumed topic's
    /// subscription exist: the receiver provisions in the background, and a message published before
    /// its subscription exists would be dropped by the topic.
    /// </summary>
    private async Task StartHostAsync(string queueName)
    {
        var configuration = ComposeHostConfiguration(ConnectionString, AdminEndpoint, queueName);
        ConfigureHost(configuration);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = queueName });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddLogging();
        builder.Services.AddAzureServiceBusTransport();

        var services = builder.Services;
        builder.Services.AddBrokerMessaging(builder.Configuration, consumers => ConfigureConsumers(consumers, services));

        TransportHost = builder.Build();
        await TransportHost.StartAsync().ConfigureAwait(false);

        var topology = TransportHost.Services.GetRequiredService<BrokerTopology>();
        foreach (var subscription in topology.Subscriptions)
        {
            await WaitForSubscriptionAsync(subscription.Topic, topology.QueueName).ConfigureAwait(false);
        }
    }

    private async Task WaitForSubscriptionAsync(string topic, string queueName)
    {
        // The transport's entity names: a topic or queue path fits 260 characters, a subscription
        // (named after the queue) 50.
        var topicPath = BrokerNaming.Fit(topic, 260);
        var subscriptionName = BrokerNaming.Fit(queueName, 50);

        while (!(await AdminClient.SubscriptionExistsAsync(topicPath, subscriptionName).ConfigureAwait(false)).Value)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    /// <summary>Adds every delivered message to the fixture's <see cref="Consumed"/> bag.</summary>
    private sealed class RecordingHandler<TEvent>(ConcurrentBag<object> consumed) : IIntegrationEventHandler<TEvent>
        where TEvent : IIntegrationEvent
    {
        public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            consumed.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }
}
