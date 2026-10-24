using System.Globalization;
using Amazon;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Tests.Messaging;

/// <summary>
/// The Amazon SQS/SNS transport against the core broker-transport extension point: registered by
/// <c>AddAmazonSqsTransport()</c>, selected by <c>MessageBus:Provider=AmazonSqs</c>, failing at
/// registration on a redelivery interval beyond the SQS delay cap, and building a real bus without
/// opening a connection. The wire round trip lives in the LocalStack tier.
/// </summary>
public sealed class AmazonSqsTransportTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>A complete SQS configuration with redelivery inside the cap.</summary>
    private static Dictionary<string, string?> SqsSettings(params string[] redeliveryIntervals)
    {
        Dictionary<string, string?> settings = new()
        {
            ["MessageBus:Provider"] = "AmazonSqs",
            ["MessageBus:EndpointPrefix"] = "mmca-test",
            ["MessageBus:AmazonSqs:Region"] = "eu-west-1",
            ["MessageBus:AmazonSqs:ServiceUrl"] = "http://localhost:4566",
            ["MessageBus:AmazonSqs:AccessKeyId"] = "test",
            ["MessageBus:AmazonSqs:SecretAccessKey"] = "test",
        };

        for (var i = 0; i < redeliveryIntervals.Length; i++)
        {
            settings[string.Create(CultureInfo.InvariantCulture, $"MessageBus:RedeliveryIntervalsSeconds:{i}")] = redeliveryIntervals[i];
        }

        return settings;
    }

    [Fact]
    public void Name_IsTheConfiguredProviderValue() =>
        new AmazonSqsTransport().Name.Should().Be("AmazonSqs");

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
    public async Task AddBrokerMessaging_AfterTheTransport_BuildsAnAmazonSqsBusInTheConfiguredRegion()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddAmazonSqsTransport();
        services.AddBrokerMessaging(ConfigurationWith(SqsSettings("60", "300", "900")));
        await using var provider = services.BuildServiceProvider();

        services.Should().Contain(d => d.ServiceType == typeof(IMessageBus) && d.ImplementationType == typeof(BrokerMessageBus));
        var address = provider.GetRequiredService<IBusControl>().Address;
        address.Scheme.Should().Be("amazonsqs");
        address.Host.Should().Be("eu-west-1");
    }

    [Fact]
    public void AddBrokerMessaging_WithoutTheTransport_NamesThisPackage()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationWith(SqsSettings("60")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MMCA.Common.Aws*AddAmazonSqsTransport()*");
    }

    [Fact]
    public async Task AddBrokerMessaging_WithNoRedeliveryIntervalsConfigured_BuildsOnTheSqsDefault()
    {
        // The framework default [60, 600, 3600] holds a one-hour step SQS cannot delay, so an
        // unconfigured SQS host runs on the transport's own default instead of failing.
        var services = new ServiceCollection().AddLogging().AddAmazonSqsTransport();

        services.AddBrokerMessaging(ConfigurationWith(SqsSettings()));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBusControl>().Address.Scheme.Should().Be("amazonsqs");
    }

    [Fact]
    public void AddBrokerMessaging_WithAConfiguredIntervalOverTheCap_FailsNamingTheIntervalAndTheCap()
    {
        var services = new ServiceCollection().AddLogging().AddAmazonSqsTransport();

        var act = () => services.AddBrokerMessaging(ConfigurationWith(SqsSettings("60", "3600")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RedeliveryIntervalsSeconds contains 3600 seconds*900 seconds (15 minutes)*");
    }

    [Theory]
    [InlineData(901)]
    [InlineData(3600)]
    public void ResolveRedeliveryIntervals_RejectsAConfiguredIntervalOverTheCap(int seconds)
    {
        var act = () => AmazonSqsTransport.ResolveRedeliveryIntervals(
            new MessageBusSettings { RedeliveryIntervalsSeconds = [60, seconds] });

        act.Should().Throw<InvalidOperationException>().WithMessage(string.Create(CultureInfo.InvariantCulture, $"*contains {seconds} seconds*"));
    }

    [Fact]
    public void ResolveRedeliveryIntervals_AcceptsTheCapItself_AnEmptyList_AndDroppedEntries()
    {
        AmazonSqsTransport.ResolveRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [60, 300, 900] })
            .Should().Equal(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(900));
        AmazonSqsTransport.ResolveRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [] }).Should().BeEmpty();
        AmazonSqsTransport.ResolveRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [0, -1] }).Should().BeEmpty();
    }

    [Fact]
    public void ResolveRedeliveryIntervals_BoundFromConfiguration_UsesExactlyTheConfiguredList()
    {
        var configuration = ConfigurationWith(SqsSettings("60", "120", "240"));
        var bound = configuration.GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()!;

        AmazonSqsTransport.ResolveRedeliveryIntervals(bound)
            .Should().Equal(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void ResolveRedeliveryIntervals_BoundFromConfiguration_AZeroEntryTurnsRedeliveryOff()
    {
        var configuration = ConfigurationWith(SqsSettings("0"));
        var bound = configuration.GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()!;

        AmazonSqsTransport.ResolveRedeliveryIntervals(bound).Should().BeEmpty();
    }

    [Fact]
    public void ResolveRedeliveryIntervals_WithNothingConfigured_UsesTheSqsDefaultInsideTheCap()
    {
        AmazonSqsTransport.ResolveRedeliveryIntervals(new MessageBusSettings())
            .Should().Equal(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(900));

        AmazonSqsTransport.ResolveRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [30] })
            .Should().Equal(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ResolveRegion_PrefersTheConfiguredRegion_ThenTheFallbackChain()
    {
        AmazonSqsTransport.ResolveRegion("us-west-2", () => RegionEndpoint.EUCentral1).Should().Be(RegionEndpoint.USWest2);
        AmazonSqsTransport.ResolveRegion(null, () => RegionEndpoint.EUCentral1).Should().Be(RegionEndpoint.EUCentral1);
        AmazonSqsTransport.ResolveRegion("  ", () => RegionEndpoint.EUCentral1).Should().Be(RegionEndpoint.EUCentral1);
    }

    [Fact]
    public void Configure_WithNoRegionAnywhere_FailsNamingTheKey()
    {
        var transport = new AmazonSqsTransport(() => null);
        var context = new BrokerTransportContext(
            new MessageBusSettings { RedeliveryIntervalsSeconds = [] },
            null,
            ConfigurationWith(new()));

        var act = () => transport.Configure(Moq.Mock.Of<IBusRegistrationConfigurator>(), context);

        act.Should().Throw<InvalidOperationException>().WithMessage("*MessageBus:AmazonSqs:Region*");
    }

    [Theory]
    [InlineData("test", null)]
    [InlineData(null, "test")]
    public void Configure_WithHalfAKeyPair_FailsNamingBothKeys(string? accessKeyId, string? secret)
    {
        var transport = new AmazonSqsTransport(() => null);
        var context = new BrokerTransportContext(
            new MessageBusSettings { RedeliveryIntervalsSeconds = [] },
            null,
            ConfigurationWith(new()
            {
                ["MessageBus:AmazonSqs:Region"] = "us-east-1",
                ["MessageBus:AmazonSqs:AccessKeyId"] = accessKeyId,
                ["MessageBus:AmazonSqs:SecretAccessKey"] = secret,
            }));

        var act = () => transport.Configure(Moq.Mock.Of<IBusRegistrationConfigurator>(), context);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MessageBus:AmazonSqs:AccessKeyId and MessageBus:AmazonSqs:SecretAccessKey must be set together*");
    }

    [Theory]
    [InlineData("MMCA-Tenant-Id", false)]
    [InlineData("MMCA-User-Permissions", false)]
    [InlineData("mmca-correlation-id", false)]
    [InlineData("Content-Type", true)]
    [InlineData("MT-Redelivery-Count", true)]
    [InlineData("traceparent", true)]
    public void IsTransportHeader_KeepsTheAmbientHeadersInTheEnvelopeOnly(string key, bool copied) =>
        AmazonSqsTransport.IsTransportHeader(new HeaderValue<string>(key, "value")).Should().Be(copied);

    [Fact]
    public void Configure_RejectsNullArguments()
    {
        var transport = new AmazonSqsTransport();
        var context = new BrokerTransportContext(new MessageBusSettings(), null, new ConfigurationBuilder().Build());

        var noConfigurator = () => transport.Configure(null!, context);
        var noContext = () => transport.Configure(Moq.Mock.Of<IBusRegistrationConfigurator>(), null!);
        var noFallback = () => new AmazonSqsTransport(null!);

        noConfigurator.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
        noFallback.Should().Throw<ArgumentNullException>();
    }
}
