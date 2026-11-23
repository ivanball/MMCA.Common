using Amazon;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Tests.Messaging;

/// <summary>
/// The Amazon SQS/SNS transport against the core broker-transport extension point: registered by
/// <c>AddAmazonSqsTransport()</c>, selected by <c>MessageBus:Provider=AmazonSqs</c>, declaring the SQS
/// delay cap, and validating its configuration (region, key pair) while <c>AddBrokerMessaging</c> runs.
/// </summary>
public sealed class AmazonSqsTransportTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string?> SqsSettings() => new()
    {
        ["MessageBus:Provider"] = "AmazonSqs",
        ["MessageBus:QueueName"] = "mmca-test",
        ["MessageBus:EnableInbox"] = "false",
        ["Application:Namespace"] = "mmca-test",
        ["MessageBus:AmazonSqs:Region"] = "eu-west-1",
        ["MessageBus:AmazonSqs:ServiceUrl"] = "http://localhost:4566",
        ["MessageBus:AmazonSqs:AccessKeyId"] = "test",
        ["MessageBus:AmazonSqs:SecretAccessKey"] = "test",
    };

    private static BrokerTransportContext ContextWith(Dictionary<string, string?> settings) =>
        new(new MessageBusSettings(), null, ConfigurationWith(settings), new BrokerTopology("demo", "orders", []));

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
    public async Task AddBrokerMessaging_AfterTheTransport_RegistersTheSnsPublisherAndTheSqsReceiver()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAmazonSqsTransport();

        services.AddBrokerMessaging(ConfigurationWith(SqsSettings()));

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<AmazonSqsPublisher>();
        provider.GetServices<IHostedService>().OfType<AmazonSqsReceiver>().Should().ContainSingle();
        var connection = provider.GetRequiredService<AmazonSqsConnection>();
        connection.Sqs.Config.ServiceURL.Should().Be("http://localhost:4566/");
        connection.Sns.Config.ServiceURL.Should().Be("http://localhost:4566/");
        connection.Sqs.Config.AuthenticationRegion.Should().Be("eu-west-1");
    }

    [Fact]
    public void AddBrokerMessaging_WithoutTheTransport_NamesThisPackage()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationWith(SqsSettings()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MMCA.Common.Aws*AddAmazonSqsTransport()*");
    }

    [Theory]
    [InlineData("test", null)]
    [InlineData(null, "test")]
    public void Register_WithHalfAKeyPair_FailsNamingBothKeys(string? accessKeyId, string? secret)
    {
        var settings = SqsSettings();
        settings["MessageBus:AmazonSqs:AccessKeyId"] = accessKeyId;
        settings["MessageBus:AmazonSqs:SecretAccessKey"] = secret;

        var act = () => new AmazonSqsTransport(() => null).Register(new ServiceCollection(), ContextWith(settings));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MessageBus:AmazonSqs:AccessKeyId and MessageBus:AmazonSqs:SecretAccessKey must be set together*");
    }

    [Theory]
    [InlineData("test", null)]
    [InlineData(null, "test")]
    public void AddBrokerMessaging_WithHalfAKeyPair_FailsAtStartup(string? accessKeyId, string? secret)
    {
        var settings = SqsSettings();
        settings["MessageBus:AmazonSqs:AccessKeyId"] = accessKeyId;
        settings["MessageBus:AmazonSqs:SecretAccessKey"] = secret;
        var services = new ServiceCollection().AddLogging();
        services.AddAmazonSqsTransport();

        var act = () => services.AddBrokerMessaging(ConfigurationWith(settings));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be set together*");
    }

    [Fact]
    public async Task Register_WithNoKeyPair_UsesTheDefaultCredentialChain()
    {
        var settings = SqsSettings();
        settings.Remove("MessageBus:AmazonSqs:AccessKeyId");
        settings.Remove("MessageBus:AmazonSqs:SecretAccessKey");
        var services = new ServiceCollection().AddLogging();
        services.AddAmazonSqsTransport();

        services.AddBrokerMessaging(ConfigurationWith(settings));

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<AmazonSqsPublisher>();
    }

    [Fact]
    public void Register_WithNoRegionAnywhere_FailsNamingTheKey()
    {
        var settings = SqsSettings();
        settings.Remove("MessageBus:AmazonSqs:Region");

        var act = () => new AmazonSqsTransport(() => null).Register(new ServiceCollection(), ContextWith(settings));

        act.Should().Throw<InvalidOperationException>().WithMessage("*MessageBus:AmazonSqs:Region*");
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
    public void ReadSettings_BindsTheSection_OrDefaults()
    {
        var bound = AmazonSqsTransport.ReadSettings(ConfigurationWith(SqsSettings()));
        bound.Region.Should().Be("eu-west-1");
        bound.ServiceUrl.Should().Be(new Uri("http://localhost:4566"));

        AmazonSqsTransport.ReadSettings(ConfigurationWith([])).Region.Should().BeNull();
    }

    [Fact]
    public void Connection_WithoutAServiceUrl_TargetsTheRegionEndpoint()
    {
        AmazonSqsConnection.CreateSqsConfig(RegionEndpoint.USEast2, null).RegionEndpoint.Should().Be(RegionEndpoint.USEast2);
        AmazonSqsConnection.CreateSnsConfig(RegionEndpoint.USEast2, null).RegionEndpoint.Should().Be(RegionEndpoint.USEast2);
    }

    [Fact]
    public void Register_RejectsNullArguments()
    {
        var transport = new AmazonSqsTransport();
        var context = ContextWith(SqsSettings());

        var noServices = () => transport.Register(null!, context);
        var noContext = () => transport.Register(new ServiceCollection(), null!);
        var noFallback = () => new AmazonSqsTransport(null!);

        noServices.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
        noFallback.Should().Throw<ArgumentNullException>();
    }
}
