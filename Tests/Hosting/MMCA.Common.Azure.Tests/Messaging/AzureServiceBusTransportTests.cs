using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// The Azure Service Bus transport against the core broker-transport extension point: registered by
/// <c>AddAzureServiceBusTransport()</c> and selected by <c>MessageBus:Provider=AzureServiceBus</c>. Its
/// <c>Register</c> is a placeholder on <c>feat/native-messaging</c> until the native transport lands.
/// </summary>
public sealed class AzureServiceBusTransportTests
{
    private const string NamespaceConnectionString =
        "Endpoint=sb://mmca-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=cmVkYWN0ZWQ=";

    private static IConfiguration ConfigurationFor(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = provider,
                ["MessageBus:ConnectionString"] = NamespaceConnectionString,
                ["applicationName"] = "Azure.Tests.Service",
            })
            .Build();

    [Fact]
    public void Name_IsTheConfiguredProviderValue() =>
        new AzureServiceBusTransport().Name.Should().Be("AzureServiceBus");

    [Fact]
    public void MaxRedeliveryDelay_IsUncapped() =>
        new AzureServiceBusTransport().MaxRedeliveryDelay.Should().BeNull();

    [Fact]
    public void AddAzureServiceBusTransport_RegistersTheTransportOnce()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBusTransport();
        services.AddAzureServiceBusTransport();

        services.Where(d => d.ServiceType == typeof(IBrokerTransport))
            .Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeOfType<AzureServiceBusTransport>();
    }

    [Fact]
    public void AddBrokerMessaging_AfterTheTransport_SelectsIt()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAzureServiceBusTransport();

        var act = () => services.AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        act.Should().Throw<NotSupportedException>().WithMessage("Azure Service Bus transport*");
    }

    [Fact]
    public void AddBrokerMessaging_WithoutTheTransport_NamesThisPackage()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MMCA.Common.Azure*AddAzureServiceBusTransport()*");
    }

    [Fact]
    public void Register_RejectsNullArguments()
    {
        var transport = new AzureServiceBusTransport();
        var context = new BrokerTransportContext(
            new MessageBusSettings(), null, new ConfigurationBuilder().Build(), new BrokerTopology("demo", "orders", []));

        var noServices = () => transport.Register(null!, context);
        var noContext = () => transport.Register(new ServiceCollection(), null!);

        noServices.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
    }
}
