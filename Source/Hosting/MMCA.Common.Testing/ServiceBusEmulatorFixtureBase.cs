using System.Collections.Concurrent;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using MassTransit;
using Testcontainers.ServiceBus;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Shared scaffolding for the Azure Service Bus emulator <b>broker-parity</b> test tier: a collection
/// fixture that starts ONE warm emulator (plus its companion SQL Server container, provisioned
/// automatically by <c>Testcontainers.ServiceBus</c>) for the whole tier and, optionally, owns the one
/// MassTransit bus every test publishes through. One shared container respects the emulator's
/// 10-connection namespace quota and its roughly one-admin-operation-per-second throttle: each bus
/// start provisions topology through the admin plane, so serial tests against one warm emulator are
/// the stable shape.
/// <para>
/// The image is pinned (<see cref="DefaultEmulatorImage"/>): the admin plane
/// (<see cref="ServiceBusAdministrationClient"/> over HTTP port 5300) shipped in emulator 2.0.0 and is
/// what lets MassTransit v8 create its own topics, subscriptions and queues, so a silent downgrade to a
/// 1.x image would break the tier's whole premise. The static constructor lowers MassTransit's
/// process-global entity defaults beneath the emulator's one-hour maximum TTL quota (v8's defaults,
/// 366d TTL / 427d auto-delete, are rejected by the emulator); that override is process-wide, which is
/// why this tier belongs in its OWN test process.
/// </para>
/// <para>
/// <b>The bus lives on the FIXTURE, never on the test class.</b> A test class implementing
/// <see cref="IAsyncLifetime"/> is re-instantiated per <c>[Fact]</c>, so a bus created there starts once
/// PER TEST and every start re-provisions the whole topology (a topic per message type, a subscription
/// each, and the receive-endpoint queue) through that throttled admin plane. With two contracts and two
/// tests that was four provisioning cycles per run, and MMCA.ADC's job hung and was killed at its
/// timeout on 7 of 7 scheduled runs (2026-07-21 to 2026-07-24). Hoisting the bus here makes it one cycle
/// per run. For the same reason the base provisions exactly ONE receive endpoint
/// (<see cref="ReceiveQueueName"/>) and lets a subclass add only handlers to it
/// (<see cref="ConfigureReceiveEndpoint"/>): every extra contract then costs a topic and a subscription
/// rather than another queue.
/// </para>
/// <para>
/// Both startup phases are wall-clock bounded. The point is not only to fail sooner: a step killed by
/// the JOB timeout has its output discarded, so a hang leaves no evidence of WHICH phase hung, which is
/// exactly what left that hang unlocalized for a week. A bounded phase throws a named
/// <see cref="TimeoutException"/> instead, the step completes, and its log survives. <c>WaitAsync</c>
/// rather than a <see cref="CancellationToken"/>, because the observed hang does not honour
/// cancellation; bounding the await is what makes the limit enforceable.
/// </para>
/// <para>
/// Stays in the subclass: the sealed fixture itself, its <c>[CollectionDefinition]</c> class (a
/// collection definition is per test assembly by construction), the integration-event contracts, and
/// the assertions.
/// </para>
/// </summary>
public abstract class ServiceBusEmulatorFixtureBase : IAsyncLifetime
{
    /// <summary>
    /// The pinned emulator image. 2.x on purpose: the HTTP management plane MassTransit provisions its
    /// topology through shipped in 2.0.0, so a 1.x image would leave the broker unusable rather than
    /// merely older.
    /// </summary>
    public const string DefaultEmulatorImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1";

    /// <summary>The emulator's HTTP management-plane container port.</summary>
    public const int AdminPlanePort = 5300;

    private ServiceBusContainer? _container;
    private ServiceBusClient? _client;
    private IBusControl? _busControl;

    static ServiceBusEmulatorFixtureBase()
    {
        // The emulator rejects entities whose TTL/auto-delete exceed its 1h quota; MassTransit v8's
        // defaults are far above it. Process-global by design (MassTransit statics).
        MassTransit.AzureServiceBusTransport.Defaults.DefaultMessageTimeToLive = TimeSpan.FromHours(1);
        MassTransit.AzureServiceBusTransport.Defaults.BasicMessageTimeToLive = TimeSpan.FromHours(1);
        MassTransit.AzureServiceBusTransport.Defaults.AutoDeleteOnIdle = TimeSpan.FromHours(1);
    }

    /// <summary>AMQP (data-plane) client bound to the running emulator.</summary>
    public ServiceBusClient Client =>
        _client ?? throw new InvalidOperationException("The Service Bus emulator has not started yet.");

    /// <summary>
    /// Admin (management-plane) client bound to the emulator's mapped HTTP 5300 endpoint. Assigned in
    /// <see cref="InitializeAsync"/>, so it is only meaningful once the fixture has started.
    /// </summary>
    public ServiceBusAdministrationClient AdminClient { get; private set; } = null!;

    /// <summary>
    /// The one bus for the whole tier, started once with its topology already provisioned. Named
    /// BusControl rather than Bus so it does not shadow MassTransit's static <c>Bus.Factory</c>. Only
    /// available when <see cref="ReceiveQueueName"/> names a queue; a fixture that wants the clients
    /// alone never touches it.
    /// </summary>
    public IBusControl BusControl =>
        _busControl ?? throw new InvalidOperationException(
            "No bus was started. Override ReceiveQueueName to have this fixture host one.");

    /// <summary>
    /// Everything the receive endpoint consumed this run. Shared across the tier because the bus is:
    /// each test matches on a value unique to itself, so one bag cannot cross-contaminate.
    /// </summary>
    public ConcurrentBag<object> Consumed { get; } = [];

    /// <summary>The host address MassTransit's custom-clients <c>Host()</c> overload is anchored to.</summary>
    public Uri HostAddress { get; } = new("sb://localhost/");

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

    /// <summary>Budget for PHASE 2, MassTransit provisioning its topology through the admin plane.</summary>
    protected virtual TimeSpan BusStartTimeout => TimeSpan.FromMinutes(3);

    /// <summary>Budget for stopping the bus on teardown, after which the container is torn down anyway.</summary>
    protected virtual TimeSpan BusStopTimeout => TimeSpan.FromMinutes(1);

    /// <summary>
    /// The single receive-endpoint queue every contract in the tier is bound to, or
    /// <see langword="null"/> (the default) for a fixture that wants only <see cref="Client"/> and
    /// <see cref="AdminClient"/> and no bus at all.
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

        _client = new ServiceBusClient(_container.GetConnectionString());
        AdminClient = new ServiceBusAdministrationClient(
            ComposeAdminConnectionString(_container.Hostname, _container.GetMappedPublicPort(AdminPlanePort)));

        var queueName = ReceiveQueueName;
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return;
        }

        _busControl = Bus.Factory.CreateUsingAzureServiceBus(cfg =>
        {
            // The v8 custom-clients overload: the only v8 path onto the emulator (vendor emulator
            // support shipped in v9, excluded by the MassTransit-v8 policy pin). Both clients are
            // pre-built against the emulator's mapped ports above.
            cfg.Host(HostAddress, Client, AdminClient);

            // ONE receive endpoint for the whole tier, so the queue is provisioned once and each
            // contract the subclass binds adds only its own topic plus subscription.
            cfg.ReceiveEndpoint(queueName, ConfigureReceiveEndpoint);
        });

        try
        {
            await _busControl.StartAsync().WaitAsync(BusStartTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"PHASE 2 (topology): the container started, but MassTransit did not finish provisioning the " +
                $"topology within {BusStartTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes. The container is therefore NOT " +
                "the culprit; the admin plane is. Provisioning is already down to one cycle per run, so the next " +
                "lever is fewer entities (one contract) or an explicit pre-provision before the bus starts.",
                ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        // Best effort, in reverse order of creation. A failure here must not mask a real test failure,
        // and on either InitializeAsync timeout path the later members were never assigned.
        if (_busControl is not null)
        {
            try
            {
                await _busControl.StopAsync().WaitAsync(BusStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The container is torn down next, which takes the whole namespace with it.
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
    /// Binds the tier's contracts to the single receive endpoint: one
    /// <c>endpoint.Handler&lt;TContract&gt;</c> per contract, each adding its message to
    /// <see cref="Consumed"/>. Defaults to no handler, which provisions the queue and nothing else.
    /// </summary>
    /// <param name="endpoint">The receive-endpoint configurator for <see cref="ReceiveQueueName"/>.</param>
    protected virtual void ConfigureReceiveEndpoint(IServiceBusReceiveEndpointConfigurator endpoint)
    {
    }
}
