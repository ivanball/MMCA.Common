using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// The Azure Service Bus transport against the core broker-transport extension point: registered by
/// <c>AddAzureServiceBusTransport()</c>, selected by <c>MessageBus:Provider=AzureServiceBus</c>, and
/// building a real bus without opening a connection. The emulator branch is pinned by
/// <see cref="ServiceBusEmulatorSupportTests"/>; nothing here enters it, because it rewrites
/// process-global MassTransit defaults.
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
            })
            .Build();

    [Fact]
    public void Name_IsTheConfiguredProviderValue() =>
        new AzureServiceBusTransport().Name.Should().Be("AzureServiceBus");

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
    public async Task AddBrokerMessaging_AfterTheTransport_BuildsAnAzureServiceBusBus()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddAzureServiceBusTransport();
        services.AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));
        await using var provider = services.BuildServiceProvider();

        services.Should().Contain(d => d.ServiceType == typeof(IMessageBus) && d.ImplementationType == typeof(BrokerMessageBus));
        provider.GetRequiredService<IBusControl>().Address.Host.Should().Be("mmca-test.servicebus.windows.net");
    }

    [Fact]
    public void AddBrokerMessaging_WithoutTheTransport_NamesThisPackage()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MMCA.Common.Azure*AddAzureServiceBusTransport()*");
    }

    [Fact]
    public void Configure_RejectsNullArguments()
    {
        var transport = new AzureServiceBusTransport();
        var context = new BrokerTransportContext(new MessageBusSettings(), null, new ConfigurationBuilder().Build());

        var noConfigurator = () => transport.Configure(null!, context);
        var noContext = () => transport.Configure(Moq.Mock.Of<IBusRegistrationConfigurator>(), null!);

        noConfigurator.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
    }
}
