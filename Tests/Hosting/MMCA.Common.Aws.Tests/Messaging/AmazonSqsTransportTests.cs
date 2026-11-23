using Amazon;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Tests.Messaging;

/// <summary>
/// The Amazon SQS/SNS transport against the core broker-transport extension point: registered by
/// <c>AddAmazonSqsTransport()</c>, selected by <c>MessageBus:Provider=AmazonSqs</c>, declaring the SQS
/// delay cap, and resolving its region. Its <c>Register</c> is a placeholder on
/// <c>feat/native-messaging</c> until the native transport lands.
/// </summary>
public sealed class AmazonSqsTransportTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string?> SqsSettings() => new()
    {
        ["MessageBus:Provider"] = "AmazonSqs",
        ["MessageBus:QueueName"] = "mmca-test",
        ["MessageBus:AmazonSqs:Region"] = "eu-west-1",
        ["MessageBus:AmazonSqs:ServiceUrl"] = "http://localhost:4566",
        ["MessageBus:AmazonSqs:AccessKeyId"] = "test",
        ["MessageBus:AmazonSqs:SecretAccessKey"] = "test",
    };

    [Fact]
    public void Name_IsTheConfiguredProviderValue() =>
        new AmazonSqsTransport().Name.Should().Be("AmazonSqs");

    [Fact]
    public void MaxRedeliveryDelay_IsTheSqsDelayCap() =>
        new AmazonSqsTransport().MaxRedeliveryDelay.Should().Be(TimeSpan.FromMinutes(15));

    [Fact]
    public void MaxRedeliveryDelay_ClampsTheFrameworkDefaultIntervals() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings(), new AmazonSqsTransport().MaxRedeliveryDelay)
            .Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));

    [Fact]
    public void AddAmazonSqsTransport_RegistersTheTransportOnce()
    {
        var services = new ServiceCollection();

        services.AddAmazonSqsTransport();
        services.AddAmazonSqsTransport();

        services.Where(d => d.ServiceType == typeof(IBrokerTransport))
            .Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeOfType<AmazonSqsTransport>();
    }

    [Fact]
    public void AddBrokerMessaging_AfterTheTransport_SelectsIt()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAmazonSqsTransport();

        var act = () => services.AddBrokerMessaging(ConfigurationWith(SqsSettings()));

        act.Should().Throw<NotSupportedException>().WithMessage("Amazon SQS transport*");
    }

    [Fact]
    public void AddBrokerMessaging_WithoutTheTransport_NamesThisPackage()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationWith(SqsSettings()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MMCA.Common.Aws*AddAmazonSqsTransport()*");
    }

    [Fact]
    public void ResolveRegion_PrefersTheConfiguredRegion_ThenTheFallbackChain()
    {
        AmazonSqsTransport.ResolveRegion("us-west-2", () => RegionEndpoint.EUCentral1).Should().Be(RegionEndpoint.USWest2);
        AmazonSqsTransport.ResolveRegion(null, () => RegionEndpoint.EUCentral1).Should().Be(RegionEndpoint.EUCentral1);
        AmazonSqsTransport.ResolveRegion("  ", () => RegionEndpoint.EUCentral1).Should().Be(RegionEndpoint.EUCentral1);
    }

    [Fact]
    public void ResolveRegion_FromConfiguration_UsesTheConfiguredKeyThenTheInstanceFallback()
    {
        new AmazonSqsTransport(() => RegionEndpoint.EUCentral1).ResolveRegion(ConfigurationWith(SqsSettings()))
            .Should().Be(RegionEndpoint.EUWest1);
        new AmazonSqsTransport(() => RegionEndpoint.EUCentral1).ResolveRegion(ConfigurationWith([]))
            .Should().Be(RegionEndpoint.EUCentral1);
    }

    [Fact]
    public void ResolveRegion_WithNoRegionAnywhere_FailsNamingTheKey()
    {
        var act = () => new AmazonSqsTransport(() => null).ResolveRegion(ConfigurationWith([]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*MessageBus:AmazonSqs:Region*");
    }

    [Fact]
    public void Register_RejectsNullArguments()
    {
        var transport = new AmazonSqsTransport();
        var context = new BrokerTransportContext(
            new MessageBusSettings(), null, new ConfigurationBuilder().Build(), new BrokerTopology("demo", "orders", []));

        var noServices = () => transport.Register(null!, context);
        var noContext = () => transport.Register(new ServiceCollection(), null!);
        var noFallback = () => new AmazonSqsTransport(null!);

        noServices.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
        noFallback.Should().Throw<ArgumentNullException>();
    }
}
