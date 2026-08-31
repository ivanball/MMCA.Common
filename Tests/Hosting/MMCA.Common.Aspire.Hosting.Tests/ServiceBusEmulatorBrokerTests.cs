using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for the Azure Service Bus emulator broker: the container resource
/// <c>AddServiceBusEmulatorBroker</c> provisions, and what the matching <c>WithBroker</c> overload
/// injects into a consuming service. They assert the application MODEL rather than a running stack:
/// the emulator needs Docker and a warm-up measured in tens of seconds, which belongs in a consumer's
/// integration tier, while everything that can silently break the wiring (image pin, endpoint names
/// and ports, the connection-string form, the environment keys) is decided here.
/// </summary>
public sealed class ServiceBusEmulatorBrokerTests
{
    [Fact]
    public void AddServiceBusEmulatorBroker_PinsTheImageToATwoPointXEmulator()
    {
        // The HTTP management plane shipped in emulator 2.0.0, and MassTransit provisions its whole
        // topology through it at bus start. A silent downgrade to a 1.x image would leave the broker
        // unusable rather than merely older.
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));

        var image = emulator.Resource.Annotations.OfType<ContainerImageAnnotation>().Should().ContainSingle().Subject;
        image.Registry.Should().Be("mcr.microsoft.com");
        image.Image.Should().Be("azure-messaging/servicebus-emulator");
        image.Tag.Should().Be("2.0.1");
        Extensions.ServiceBusEmulatorImageTag.Should().StartWith("2.");
    }

    [Fact]
    public void AddServiceBusEmulatorBroker_PublishesBothPlanes()
    {
        // AMQP carries every publish and consume; the management plane is what MassTransit creates
        // its topics, subscriptions and queues through. A broker with only the first is not usable.
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));

        List<EndpointAnnotation> endpoints = [.. emulator.Resource.Annotations.OfType<EndpointAnnotation>()];

        var amqp = endpoints.Should().ContainSingle(e => e.Name == ServiceBusEmulatorResource.AmqpEndpointName).Subject;
        amqp.TargetPort.Should().Be(ServiceBusEmulatorResource.AmqpTargetPort);

        var admin = endpoints.Should().ContainSingle(e => e.Name == ServiceBusEmulatorResource.AdminEndpointName).Subject;
        admin.TargetPort.Should().Be(ServiceBusEmulatorResource.AdminTargetPort);
        admin.UriScheme.Should().Be("http");
    }

    [Fact]
    public void AddServiceBusEmulatorBroker_LeavesHostPortsToAspire()
    {
        // Nothing outside the stack dials these, so fixing a host port would only invite a collision
        // with a stray container from another run.
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));

        emulator.Resource.Annotations.OfType<EndpointAnnotation>()
            .Should().OnlyContain(e => e.Port == null);
    }

    [Fact]
    public async Task AddServiceBusEmulatorBroker_WiresTheEmulatorToTheGivenSqlServer()
    {
        // The emulator keeps its state in SQL Server. It is pointed at an EXISTING resource so a
        // stack that already runs SQL Server for its databases does not run a second engine for the
        // broker, and it is handed the host name alone: the emulator dials the default port 1433,
        // which is the container's target port.
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));

        Dictionary<string, string> environment = await EnvironmentOf(builder, emulator.Resource);

        environment.Should().ContainKey("ACCEPT_EULA").WhoseValue.Should().Be("Y");
        environment.Should().ContainKey("SQL_SERVER").WhoseValue.Should().Be("{sql.bindings.tcp.host}");
        environment.Should().ContainKey("MSSQL_SA_PASSWORD").WhoseValue.Should().Be("{sql-password.value}");
    }

    [Fact]
    public void AddServiceBusEmulatorBroker_WaitsForSqlServer()
    {
        // The emulator's first act is to create its schema, so it cannot start before SQL Server is
        // accepting connections.
        var builder = DistributedApplication.CreateBuilder([]);
        var sql = builder.AddSqlServer("sql");

        var emulator = builder.AddServiceBusEmulatorBroker(sql);

        emulator.Resource.Annotations.OfType<WaitAnnotation>()
            .Should().ContainSingle(w => w.Resource == sql.Resource);
    }

    [Fact]
    public void ConnectionString_IsTheEmulatorForm_BuiltFromTheAllocatedAmqpEndpoint()
    {
        // UseDevelopmentEmulator=true is not decoration: it keeps the Azure SDK clients on plain
        // TCP/HTTP against a local host, and it is the marker the infrastructure layer keys its
        // emulator branch off. A real namespace never emits it.
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));

        emulator.Resource.ConnectionStringExpression.ValueExpression.Should().Be(
            "Endpoint=sb://{servicebus.bindings.amqp.host}:{servicebus.bindings.amqp.port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
    }

    [Fact]
    public void AdminEndpoint_IsBuiltFromTheAllocatedManagementEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));

        emulator.Resource.AdminEndpointExpression.ValueExpression.Should().Be(
            "{servicebus.bindings.admin.scheme}://{servicebus.bindings.admin.host}:{servicebus.bindings.admin.port}");
    }

    [Fact]
    public async Task WithBroker_SelectsTheAzureServiceBusTransportAndPassesBothPlanes()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));
        var service = builder.AddContainer("conference", "conference-image");

        service.WithBroker(emulator);

        Dictionary<string, string> environment = await EnvironmentOf(builder, service.Resource);

        environment.Should().ContainKey("MessageBus__Provider").WhoseValue.Should().Be("AzureServiceBus");
        environment.Should().ContainKey("MessageBus__ConnectionString")
            .WhoseValue.Should().EndWith("UseDevelopmentEmulator=true;");
        environment.Should().ContainKey("MessageBus__EmulatorAdminEndpoint")
            .WhoseValue.Should().Be("{servicebus.bindings.admin.scheme}://{servicebus.bindings.admin.host}:{servicebus.bindings.admin.port}");
    }

    [Fact]
    public void WithBroker_WaitsForTheBroker()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var emulator = builder.AddServiceBusEmulatorBroker(builder.AddSqlServer("sql"));
        var service = builder.AddContainer("conference", "conference-image");

        service.WithBroker(emulator);

        service.Resource.Annotations.OfType<WaitAnnotation>()
            .Should().ContainSingle(w => w.Resource == emulator.Resource);
    }

    [Fact]
    public async Task WithBroker_RabbitMqOverloadIsUnchanged()
    {
        // The emulator is opt-in. Adding it must not move a single byte of what the RabbitMQ path
        // injects, or every existing AppHost changes behavior on upgrade.
        var builder = DistributedApplication.CreateBuilder([]);
        var rabbit = builder.AddMessageBroker();
        var service = builder.AddContainer("conference", "conference-image");

        service.WithBroker(rabbit);

        Dictionary<string, string> environment = await EnvironmentOf(builder, service.Resource);

        environment.Should().ContainKey("MessageBus__Provider").WhoseValue.Should().Be("RabbitMq");
        environment.Should().NotContainKey("MessageBus__EmulatorAdminEndpoint");
    }

    /// <summary>
    /// Resolves a resource's environment variables in publish mode, where every reference stays a
    /// manifest expression (<c>{servicebus.bindings.amqp.host}</c>). That is what makes the assertion
    /// about WHICH endpoint a value is bound to, rather than about a port DCP happened to allocate.
    /// </summary>
    private static async Task<Dictionary<string, string>> EnvironmentOf(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = services,
            });

        IExecutionConfigurationResult result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        result.Exception.Should().BeNull();
        return result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }
}
