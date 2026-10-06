using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// The AppHost broker selector, asserted on the application MODEL: with the selector variable absent
/// (or set to anything else) the stack provisions RabbitMQ, with it set to <c>servicebus</c> it
/// provisions the Service Bus emulator instead, and the returned delegate wires a service to whichever
/// broker was chosen. Each test uses its own variable name, so parallel tests never see each other's
/// process-wide environment.
/// </summary>
public sealed class BrokerSelectionTests
{
    [Fact]
    public void UnsetVariable_SelectsRabbitMq()
    {
        var variable = UniqueVariable();
        var builder = DistributedApplication.CreateBuilder([]);

        var attach = builder.AddSelectedBroker(variable, builder.AddSqlServer("sql"));
        var service = builder.AddResource(new ProjectResource("svc")).WithSelectedBroker(attach);

        builder.Resources.OfType<RabbitMQServerResource>().Should().ContainSingle()
            .Which.Name.Should().Be(Extensions.DefaultBrokerResourceName);
        builder.Resources.OfType<ServiceBusEmulatorResource>().Should().BeEmpty();
        WaitsFor(service.Resource).Should().Contain(Extensions.DefaultBrokerResourceName);
    }

    [Fact]
    public void ServiceBusValue_InAnyCase_SelectsTheEmulator()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, "ServiceBus");
        try
        {
            var builder = DistributedApplication.CreateBuilder([]);

            var attach = builder.AddSelectedBroker(variable, builder.AddSqlServer("sql"));
            var service = builder.AddResource(new ProjectResource("svc")).WithSelectedBroker(attach);

            builder.Resources.OfType<ServiceBusEmulatorResource>().Should().ContainSingle()
                .Which.Name.Should().Be(Extensions.DefaultServiceBusEmulatorResourceName);
            builder.Resources.OfType<RabbitMQServerResource>().Should().BeEmpty();
            WaitsFor(service.Resource).Should().Contain(Extensions.DefaultServiceBusEmulatorResourceName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void AnyOtherValue_SelectsRabbitMq()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, "rabbitmq");
        try
        {
            BrokerSelection.IsServiceBusSelected(variable).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void RabbitMq_IsPersistentAcrossAppHostRestarts()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        builder.AddSelectedBroker(UniqueVariable(), builder.AddSqlServer("sql"));

        builder.Resources.OfType<RabbitMQServerResource>().Single()
            .Annotations.OfType<ContainerLifetimeAnnotation>()
            .Should().ContainSingle().Which.Lifetime.Should().Be(ContainerLifetime.Persistent);
    }

    private static string UniqueVariable() => "MMCA_TEST_BROKER_" + Guid.NewGuid().ToString("N");

    private static IEnumerable<string> WaitsFor(ProjectResource resource) =>
        resource.Annotations.OfType<WaitAnnotation>().Select(static w => w.Resource.Name);
}
