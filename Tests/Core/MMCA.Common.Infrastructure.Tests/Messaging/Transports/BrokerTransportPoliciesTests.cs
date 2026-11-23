using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports;

/// <summary>
/// The shared receive policy every transport and the receive pipeline read: the exponential retry
/// delay, the redelivery intervals (default, replacement, dropping, clamping) and the backpressure
/// guards.
/// </summary>
public sealed class BrokerTransportPoliciesTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void RetryDelay_DoublesFromTheMinimum_CappedAtTheMaximum(int retryNumber, int expectedSeconds) =>
        BrokerTransportPolicies.RetryDelay(new MessageBusSettings(), retryNumber)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void RetryDelay_ZeroMinimum_RetriesImmediately() =>
        BrokerTransportPolicies.RetryDelay(new MessageBusSettings { RetryMinIntervalSeconds = 0 }, 3)
            .Should().Be(TimeSpan.Zero);

    [Fact]
    public void RetryDelay_AMaximumBelowTheMinimum_NeverShortensTheMinimum() =>
        BrokerTransportPolicies.RetryDelay(new MessageBusSettings { RetryMinIntervalSeconds = 10, RetryMaxIntervalSeconds = 2 }, 1)
            .Should().Be(TimeSpan.FromSeconds(10));

    [Fact]
    public void RetryDelay_RetryNumberBelowOne_Throws()
    {
        var act = () => BrokerTransportPolicies.RetryDelay(new MessageBusSettings(), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildRedeliveryIntervals_Unconfigured_UsesTheFrameworkDefault() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings())
            .Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

    [Fact]
    public void BuildRedeliveryIntervals_AConfiguredListReplacesTheDefault() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [5, 15] })
            .Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

    [Fact]
    public void BuildRedeliveryIntervals_DropsNonPositiveEntries_SoZeroTurnsRedeliveryOff()
    {
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [0] })
            .Should().BeEmpty();
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [-1, 30, 0] })
            .Should().Equal(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void BuildRedeliveryIntervals_BoundFromConfiguration_UsesExactlyTheConfiguredList()
    {
        // The binder appends to a non-null initializer; the property has none, so the configured list
        // is the whole list.
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:RedeliveryIntervalsSeconds:0"] = "2",
                ["MessageBus:RedeliveryIntervalsSeconds:1"] = "4",
            })
            .Build()
            .GetSection(MessageBusSettings.SectionName)
            .Get<MessageBusSettings>()!;

        BrokerTransportPolicies.BuildRedeliveryIntervals(settings)
            .Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void BuildRedeliveryIntervals_ClampsEveryIntervalToTheTransportCap() =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings(), TimeSpan.FromMinutes(15))
            .Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BuildRedeliveryIntervals_ANonPositiveCap_IsIgnored(int capSeconds) =>
        BrokerTransportPolicies.BuildRedeliveryIntervals(new MessageBusSettings { RedeliveryIntervalsSeconds = [7200] }, TimeSpan.FromSeconds(capSeconds))
            .Should().Equal(TimeSpan.FromHours(2));

    [Fact]
    public void BuildRedeliveryIntervals_NullSettings_Throws()
    {
        var act = () => BrokerTransportPolicies.BuildRedeliveryIntervals(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(16, 16)]
    public void ResolvePrefetchCount_KeepsOnlyAPositiveValue(int? configured, int? expected) =>
        BrokerTransportPolicies.ResolvePrefetchCount(new MessageBusSettings { PrefetchCount = configured })
            .Should().Be(expected);

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(8, 8)]
    public void ResolveConcurrentMessageLimit_KeepsOnlyAPositiveValue(int? configured, int? expected) =>
        BrokerTransportPolicies.ResolveConcurrentMessageLimit(new MessageBusSettings { ConcurrentMessageLimit = configured })
            .Should().Be(expected);
}
