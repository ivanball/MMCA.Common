using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports;

/// <summary>
/// The shared receive policies every broker transport applies. They are public so an adapter
/// transport applies exactly what the built-in one does; these tests pin the mapping from
/// <see cref="MessageBusSettings"/> and the guards that keep a bad value away from the transport.
/// </summary>
public sealed class BrokerTransportPoliciesTests
{
    [Fact]
    public void BuildRedeliveryIntervals_DropsNonPositiveEntries_AndKeepsTheOrder() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [60, 0, -5, 600] })
            .Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

    [Fact]
    public void BuildRedeliveryIntervals_DefaultsToOneMinuteTenMinutesOneHour() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings())
            .Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

    [Fact]
    public void BuildRedeliveryIntervals_NothingPositive_ReturnsEmpty() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [0, -1] })
            .Should().BeEmpty();

    [Fact]
    public void BuildRedeliveryIntervals_BoundFromConfiguration_ConfiguredListReplacesTheDefault()
    {
        // The exact binding AddBrokerMessaging performs. A configured list must REPLACE the default,
        // not be appended to it: an append makes the defaults impossible to shorten by configuration.
        var bound = Bind(new Dictionary<string, string?>
        {
            ["MessageBus:RedeliveryIntervalsSeconds:0"] = "60",
            ["MessageBus:RedeliveryIntervalsSeconds:1"] = "300",
            ["MessageBus:RedeliveryIntervalsSeconds:2"] = "900",
        });

        bound.RedeliveryIntervalsSeconds.Should().Equal(60, 300, 900);
        BrokerTransportPolicies.BuildRedeliveryIntervals(bound)
            .Should().Equal(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(900));
    }

    [Fact]
    public void BuildRedeliveryIntervals_BoundFromConfiguration_UnconfiguredYieldsTheDefault() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(Bind(new Dictionary<string, string?> { ["MessageBus:Provider"] = "RabbitMq" }))
            .Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

    [Fact]
    public void BuildRedeliveryIntervals_BoundFromConfiguration_AZeroEntryTurnsRedeliveryOff() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(Bind(new Dictionary<string, string?> { ["MessageBus:RedeliveryIntervalsSeconds:0"] = "0" }))
            .Should().BeEmpty();

    [Fact]
    public void BuildRedeliveryIntervals_WithATransportDefault_AppliesItOnlyWhenNothingIsConfigured()
    {
        int[] transportDefault = [30, 90];

        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings(), transportDefault)
            .Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [5, 0] }, transportDefault)
            .Should().Equal(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ApplyBackpressure_PositiveValues_ReachTheConfigurator()
    {
        var cfg = new Mock<IBusFactoryConfigurator>();

        BrokerTransportPolicies.ApplyBackpressure(cfg.Object, new MessageBusSettings { PrefetchCount = 16, ConcurrentMessageLimit = 8 });

        cfg.VerifySet(c => c.PrefetchCount = 16, Times.Once());
        cfg.VerifySet(c => c.ConcurrentMessageLimit = 8, Times.Once());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ApplyBackpressure_UnsetOrNonPositive_LeavesTheTransportDefault(int? value)
    {
        var cfg = new Mock<IBusFactoryConfigurator>();

        BrokerTransportPolicies.ApplyBackpressure(cfg.Object, new MessageBusSettings { PrefetchCount = value, ConcurrentMessageLimit = value });

        cfg.VerifySet(c => c.PrefetchCount = It.IsAny<int>(), Times.Never());
        cfg.VerifySet(c => c.ConcurrentMessageLimit = It.IsAny<int?>(), Times.Never());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyReceivePolicies_BuildsABusWithOrWithoutRedelivery(bool applyDelayedRedelivery)
    {
        var settings = new MessageBusSettings { PrefetchCount = 4 };

        var bus = Bus.Factory.CreateUsingInMemory(cfg =>
        {
            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery);
            cfg.ReceiveEndpoint("policies-probe", e => e.Handler<ProbeMessage>(_ => Task.CompletedTask));
        });

        var probe = System.Text.Json.JsonSerializer.Serialize(bus.GetProbeResult());
        probe.Should().ContainEquivalentOf("retry");
        probe.Should().Contain("\"prefetchCount\":4");
        if (applyDelayedRedelivery)
        {
            probe.Should().ContainEquivalentOf("redeliver");
        }
        else
        {
            probe.Should().NotContainEquivalentOf("redeliver");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyReceivePolicies_WithExplicitIntervals_RegistersRedeliveryOnlyWhenThereAreAny(bool withIntervals)
    {
        var settings = new MessageBusSettings();
        TimeSpan[] intervals = withIntervals ? [TimeSpan.FromSeconds(30)] : [];

        var bus = Bus.Factory.CreateUsingInMemory(cfg =>
        {
            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, intervals);
            cfg.ReceiveEndpoint("policies-probe-explicit", e => e.Handler<ProbeMessage>(_ => Task.CompletedTask));
        });

        var probe = System.Text.Json.JsonSerializer.Serialize(bus.GetProbeResult());
        probe.Should().ContainEquivalentOf("retry");
        if (withIntervals)
        {
            probe.Should().ContainEquivalentOf("redeliver");
        }
        else
        {
            probe.Should().NotContainEquivalentOf("redeliver");
        }
    }

    [Fact]
    public void Helpers_RejectNullArguments()
    {
        var cfg = Mock.Of<IBusFactoryConfigurator>();
        var settings = new MessageBusSettings();

        Action[] calls =
        [
            () => BrokerTransportPolicies.ApplyReceivePolicies(null!, settings, applyDelayedRedelivery: true),
            () => BrokerTransportPolicies.ApplyReceivePolicies(cfg, null!, applyDelayedRedelivery: true),
            () => BrokerTransportPolicies.ApplyMessageRetry(null!, settings),
            () => BrokerTransportPolicies.ApplyMessageRetry(cfg, null!),
            () => BrokerTransportPolicies.ApplyBackpressure(null!, settings),
            () => BrokerTransportPolicies.ApplyBackpressure(cfg, null!),
            () => BrokerTransportPolicies.BuildRedeliveryIntervals(null!),
            () => BrokerTransportPolicies.ApplyReceivePolicies(null!, settings, Array.Empty<TimeSpan>()),
            () => BrokerTransportPolicies.ApplyReceivePolicies(cfg, null!, Array.Empty<TimeSpan>()),
            () => BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, redeliveryIntervals: null!),
            () => BrokerTransportPolicies.BuildRedeliveryIntervals(null!, [60]),
            () => BrokerTransportPolicies.BuildRedeliveryIntervals(settings, null!),
        ];

        foreach (var call in calls)
        {
            call.Should().Throw<ArgumentNullException>();
        }
    }

    private static MessageBusSettings Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(MessageBusSettings.SectionName).Get<MessageBusSettings>()
            ?? new MessageBusSettings();

    /// <summary>The message the probe endpoint handles, so the consume pipe carries the policies.</summary>
    public sealed record ProbeMessage(int Id);
}
