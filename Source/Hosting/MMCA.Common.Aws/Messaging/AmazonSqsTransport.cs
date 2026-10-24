using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using MassTransit;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The Amazon SQS/SNS broker transport (<c>MessageBus:Provider=AmazonSqs</c>), registered by
/// <c>AddAmazonSqsTransport()</c> and selected by <c>AddBrokerMessaging</c>. MassTransit publishes
/// to one SNS topic per message type and each receive endpoint is an SQS queue subscribed to it.
/// <para>
/// Second-level delayed redelivery is applied UNCONDITIONALLY, for the reason the Azure Service Bus
/// transport gives: SQS delays a message natively, so there is no plugin to install, and
/// <c>MessageBus:EnableDelayedRedelivery</c> (which exists only to gate the RabbitMQ plugin) is not
/// consulted. SQS does cap any delay at 15 minutes, and MassTransit silently shortens a longer one
/// to the cap, so a configured interval over 900 seconds would quietly redeliver far sooner than
/// asked. An UNCONFIGURED <c>MessageBus:RedeliveryIntervalsSeconds</c> therefore uses
/// <see cref="DefaultRedeliveryIntervalsSeconds"/> (<c>[60, 300, 900]</c>) instead of the framework
/// default, whose one-hour step SQS cannot delay, while an EXPLICITLY configured interval over 900
/// seconds fails registration, naming it. <c>[0]</c> turns redelivery off.
/// </para>
/// <para>
/// The ambient <c>MMCA-*</c> headers <c>BrokerMessageBus</c> stamps are kept OUT of the SQS/SNS
/// message attributes. MassTransit copies every string header into an attribute by default, both
/// services reject a message with more than 10, and MassTransit's own headers (content type,
/// correlation, trace context, redelivery count) already compete for them. The headers still travel:
/// the MassTransit envelope serializer, the only one the framework configures, carries every header
/// in the message body, and <c>IntegrationEventConsumer</c> reads them from there.
/// </para>
/// </summary>
internal sealed class AmazonSqsTransport : IBrokerTransport
{
    /// <summary>The <c>MessageBus:Provider</c> value that selects this transport.</summary>
    internal const string ProviderName = "AmazonSqs";

    /// <summary>The longest delay SQS accepts on a message, in seconds (15 minutes).</summary>
    internal const int MaxDelaySeconds = 900;

    /// <summary>The prefix of the ambient-context headers kept in the envelope only.</summary>
    internal const string AmbientHeaderPrefix = "MMCA-";

    private readonly Func<RegionEndpoint?> _fallbackRegion;

    /// <summary>
    /// Gets the redelivery intervals, in seconds, an SQS host runs when it configures none: one
    /// minute, five minutes, fifteen minutes, the same three-step spread as the framework default
    /// with the last step at the SQS delay cap.
    /// </summary>
    internal static IReadOnlyList<int> DefaultRedeliveryIntervalsSeconds { get; } = [60, 300, MaxDelaySeconds];

    /// <summary>Initializes a new instance that falls back to the AWS SDK region chain.</summary>
    public AmazonSqsTransport()
        : this(FallbackRegionFactory.GetRegionEndpoint)
    {
    }

    /// <summary>Initializes a new instance with an explicit region fallback (tests).</summary>
    /// <param name="fallbackRegion">Supplies the region when <c>MessageBus:AmazonSqs:Region</c> is blank.</param>
    internal AmazonSqsTransport(Func<RegionEndpoint?> fallbackRegion) =>
        _fallbackRegion = fallbackRegion ?? throw new ArgumentNullException(nameof(fallbackRegion));

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Configure(IBusRegistrationConfigurator configurator, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Settings;
        var sqsSettings = context.Configuration.GetSection(AmazonSqsTransportSettings.SectionName).Get<AmazonSqsTransportSettings>()
            ?? new AmazonSqsTransportSettings();

        // Everything that can be wrong with the configuration is checked HERE, while
        // AddBrokerMessaging runs, rather than inside the bus-factory callback, which only runs when
        // the bus is first resolved.
        var redeliveryIntervals = ResolveRedeliveryIntervals(settings);
        var region = ResolveRegion(sqsSettings.Region, _fallbackRegion);
        var credentials = AwsStaticCredentials.Resolve(
            sqsSettings.AccessKeyId, sqsSettings.SecretAccessKey, AmazonSqsTransportSettings.SectionName);
        var serviceUrl = sqsSettings.ServiceUrl is { IsAbsoluteUri: true } url ? url.AbsoluteUri : null;

        configurator.UsingAmazonSqs((busContext, cfg) =>
        {
            cfg.Host(region.SystemName, h =>
            {
                // No credentials set leaves MassTransit's clients on the AWS default credential chain.
                if (credentials is not null)
                {
                    h.Credentials(credentials);
                }

                if (serviceUrl is not null)
                {
                    h.Config(new AmazonSQSConfig { ServiceURL = serviceUrl, AuthenticationRegion = region.SystemName });
                    h.Config(new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl, AuthenticationRegion = region.SystemName });
                }

                h.AllowTransportHeader(IsTransportHeader);
            });

            BrokerTransportPolicies.ApplyReceivePolicies(cfg, settings, redeliveryIntervals);
            cfg.ConfigureEndpoints(busContext);
        });
    }

    /// <summary>
    /// Resolves the second-level redelivery intervals: the configured list when there is one, else
    /// <see cref="DefaultRedeliveryIntervalsSeconds"/>, through
    /// <see cref="BrokerTransportPolicies.BuildRedeliveryIntervals(MessageBusSettings, IReadOnlyList{int})"/>
    /// (so non-positive entries are dropped and <c>[0]</c> turns redelivery off), then checked
    /// against the SQS delay cap.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <returns>The positive intervals, in configured order.</returns>
    /// <exception cref="InvalidOperationException">An interval is longer than 900 seconds.</exception>
    internal static TimeSpan[] ResolveRedeliveryIntervals(MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var intervals = BrokerTransportPolicies.BuildRedeliveryIntervals(settings, DefaultRedeliveryIntervalsSeconds);
        EnsureWithinDelayCap(intervals);
        return intervals;
    }

    /// <summary>
    /// Whether a header is copied into the SQS/SNS message attributes: every header except the
    /// ambient <c>MMCA-*</c> ones, which the envelope body carries (see the type remarks).
    /// </summary>
    /// <param name="header">The header MassTransit is about to copy.</param>
    /// <returns><see langword="false"/> for an <c>MMCA-*</c> header.</returns>
    internal static bool IsTransportHeader(HeaderValue<string> header) =>
        !header.Key.StartsWith(AmbientHeaderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fails when a redelivery interval the transport would schedule exceeds the SQS delay cap.
    /// Checked against the positive intervals only, so a list of non-positive entries stays the way
    /// to switch redelivery off.
    /// </summary>
    /// <param name="intervals">The intervals the transport would schedule.</param>
    /// <exception cref="InvalidOperationException">An interval is longer than 900 seconds.</exception>
    internal static void EnsureWithinDelayCap(IReadOnlyList<TimeSpan> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var tooLong = intervals.FirstOrDefault(interval => interval.TotalSeconds > MaxDelaySeconds);
        if (tooLong == default)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"MessageBus:RedeliveryIntervalsSeconds contains {tooLong.TotalSeconds:0} seconds, but Amazon SQS caps a message delay at {MaxDelaySeconds} seconds (15 minutes) and MassTransit would silently shorten it to the cap. Set every interval to {MaxDelaySeconds} or less, for example [60, 300, 900] (the {ProviderName} default when the key is not configured), or set it to [0] to turn second-level redelivery off for the {ProviderName} transport."));
    }

    /// <summary>
    /// Resolves the region: the configured system name, else the AWS SDK fallback chain, else a
    /// registration failure naming the key, because MassTransit cannot address SQS without one.
    /// </summary>
    /// <param name="configured">The configured <c>MessageBus:AmazonSqs:Region</c>.</param>
    /// <param name="fallback">The fallback chain.</param>
    /// <returns>The region endpoint.</returns>
    /// <exception cref="InvalidOperationException">No region is configured or resolvable.</exception>
    internal static RegionEndpoint ResolveRegion(string? configured, Func<RegionEndpoint?> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return RegionEndpoint.GetBySystemName(configured);
        }

        return fallback()
            ?? throw new InvalidOperationException(
                $"The {ProviderName} transport needs an AWS region: set {AmazonSqsTransportSettings.SectionName}:Region, or AWS_REGION for the AWS SDK fallback chain.");
    }
}
