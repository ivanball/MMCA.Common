using Aspire.Hosting.ApplicationModel;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// The official Azure Service Bus emulator running as a local container, so a development stack can
/// exercise the SAME transport production uses (<c>MessageBus:Provider=AzureServiceBus</c>) instead
/// of RabbitMQ. Provisioned by
/// <c>IDistributedApplicationBuilder.AddServiceBusEmulatorBroker(sqlServer, name)</c>.
/// <para>
/// The resource publishes two endpoints because the emulator serves two planes on two protocols:
/// AMQP on 5672 (the data plane every publish and consume flows through) and HTTP on 5300 (the
/// management plane that creates topics, subscriptions and queues). MassTransit provisions its own
/// topology at bus start, so a broker with no admin plane is not usable at all: the admin plane
/// shipped in emulator 2.0.0, which is why the image tag floor is 2.x.
/// </para>
/// <para>
/// <see cref="ConnectionStringExpression"/> is the emulator connection-string form, ending in
/// <c>UseDevelopmentEmulator=true</c>. That suffix is not decoration: it is what keeps the Azure SDK
/// clients on plain TCP/HTTP against a local host, and it is the marker
/// <c>MMCA.Common.Infrastructure</c> detects to take its emulator configuration branch. A real Azure
/// Service Bus connection string never carries it, so production behavior cannot be reached by
/// accident.
/// </para>
/// <para>
/// No health check is declared. A <c>WaitFor</c> on this resource therefore gates on the container
/// running, not on the emulator having finished its warm-up; the consuming service absorbs the
/// remainder, because MassTransit starts its bus in the background and reconnects rather than
/// failing host startup.
/// </para>
/// </summary>
public sealed class ServiceBusEmulatorResource : ContainerResource, IResourceWithConnectionString
{
    /// <summary>Endpoint name of the AMQP data plane (container port 5672).</summary>
    public const string AmqpEndpointName = "amqp";

    /// <summary>Endpoint name of the HTTP management plane (container port 5300).</summary>
    public const string AdminEndpointName = "admin";

    /// <summary>Container port the emulator serves AMQP on.</summary>
    public const int AmqpTargetPort = 5672;

    /// <summary>Container port the emulator serves its management plane on.</summary>
    public const int AdminTargetPort = 5300;

    /// <summary>Creates the emulator container resource.</summary>
    /// <param name="name">The Aspire resource name, which is also the container's network alias.</param>
    public ServiceBusEmulatorResource(string name)
        : base(name)
    {
        AmqpEndpoint = new EndpointReference(this, AmqpEndpointName);
        AdminEndpoint = new EndpointReference(this, AdminEndpointName);
    }

    /// <summary>Gets the AMQP data-plane endpoint.</summary>
    public EndpointReference AmqpEndpoint { get; }

    /// <summary>Gets the HTTP management-plane endpoint.</summary>
    public EndpointReference AdminEndpoint { get; }

    /// <summary>
    /// Gets the AMQP connection string in the emulator form. The shared-access key name and value are
    /// the emulator's own fixed placeholders (it authenticates nothing), and
    /// <c>UseDevelopmentEmulator=true</c> is the marker every consumer keys its emulator branch off.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create(
            $"Endpoint=sb://{AmqpEndpoint.Property(EndpointProperty.Host)}:{AmqpEndpoint.Property(EndpointProperty.Port)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    /// <summary>
    /// Gets the management-plane base address as a URL. A consumer hands this to
    /// <c>MessageBus:EmulatorAdminEndpoint</c>, which is what lets the infrastructure layer build the
    /// second (administration) client MassTransit v8 needs to provision topology on the emulator.
    /// <para>
    /// The scheme is read off the allocated endpoint rather than written literally, so the value
    /// tracks whatever the endpoint was declared as (<c>http</c> today: the emulator's management
    /// plane is cleartext by design and reachable only from the local development stack).
    /// </para>
    /// </summary>
    public ReferenceExpression AdminEndpointExpression =>
        ReferenceExpression.Create(
            $"{AdminEndpoint.Property(EndpointProperty.Scheme)}://{AdminEndpoint.Property(EndpointProperty.Host)}:{AdminEndpoint.Property(EndpointProperty.Port)}");
}
