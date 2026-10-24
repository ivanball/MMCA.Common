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
/// asked. Registration therefore fails on one, naming it; the framework default
/// (<c>[60, 600, 3600]</c>) is such a list, so an SQS host sets its own (for example
/// <c>[60, 300, 900]</c>) or <c>[0]</c> to turn redelivery off.
/// </para>
/// <para>
/// The intervals are read from the <c>MessageBus:RedeliveryIntervalsSeconds</c> section itself
/// rather than from the bound <see cref="MessageBusSettings"/>: the configuration binder APPENDS a
/// configured list to the property's <c>[60, 600, 3600]</c> initializer instead of replacing it, so
/// the bound value of a host that configured <c>[60, 300, 900]</c> still carries the one-hour
/// default and could never pass the cap. For the same reason the shared policies are applied as
/// their three parts (redelivery with these intervals, then <c>ApplyMessageRetry</c>, then
/// <c>ApplyBackpressure</c>), in the order <c>ApplyReceivePolicies</c> uses.
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
        var redeliveryIntervals = ResolveRedeliveryIntervals(settings, context.Configuration);
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

            // ApplyReceivePolicies(cfg, settings, applyDelayedRedelivery: true) in its three parts, so
            // redelivery runs on the intervals resolved above (see the type remarks). Same order:
            // redelivery outermost, then the in-process retry, then backpressure.
            if (redeliveryIntervals.Length > 0)
            {
                cfg.UseDelayedRedelivery(r => r.Intervals(redeliveryIntervals));
            }

            BrokerTransportPolicies.ApplyMessageRetry(cfg, settings);
            BrokerTransportPolicies.ApplyBackpressure(cfg, settings);
            cfg.ConfigureEndpoints(busContext);
        });
    }

    /// <summary>
    /// Resolves the second-level redelivery intervals: the list configured under
    /// <c>MessageBus:RedeliveryIntervalsSeconds</c> when there is one, read from that section on its
    /// own so the binder's append onto the <c>[60, 600, 3600]</c> initializer cannot leak the defaults
    /// in, else the settings' own value (the default). Non-positive entries are dropped exactly as
    /// <see cref="BrokerTransportPolicies.BuildRedeliveryIntervals"/> drops them, so <c>[0]</c> turns
    /// redelivery off, and the result is checked against the SQS delay cap.
    /// </summary>
    /// <param name="settings">The bound message-bus settings.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The positive intervals, in configured order.</returns>
    /// <exception cref="InvalidOperationException">An interval is longer than 900 seconds.</exception>
    internal static TimeSpan[] ResolveRedeliveryIntervals(MessageBusSettings settings, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration
            .GetSection($"{MessageBusSettings.SectionName}:{nameof(MessageBusSettings.RedeliveryIntervalsSeconds)}")
            .Get<int[]>();
        var source = configured is null ? settings : new MessageBusSettings { RedeliveryIntervalsSeconds = configured };

        EnsureRedeliveryWithinDelayCap(source);
        return BrokerTransportPolicies.BuildRedeliveryIntervals(source);
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
    /// Checked against the intervals <see cref="BrokerTransportPolicies.BuildRedeliveryIntervals"/>
    /// keeps (positive ones only), so a list of non-positive entries stays the way to switch
    /// redelivery off.
    /// </summary>
    /// <param name="settings">Settings carrying the intervals to check.</param>
    /// <exception cref="InvalidOperationException">An interval is longer than 900 seconds.</exception>
    internal static void EnsureRedeliveryWithinDelayCap(MessageBusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tooLong = BrokerTransportPolicies.BuildRedeliveryIntervals(settings)
            .FirstOrDefault(interval => interval.TotalSeconds > MaxDelaySeconds);
        if (tooLong == default)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"MessageBus:RedeliveryIntervalsSeconds contains {tooLong.TotalSeconds:0} seconds, but Amazon SQS caps a message delay at {MaxDelaySeconds} seconds (15 minutes) and MassTransit would silently shorten it to the cap. Set every interval to {MaxDelaySeconds} or less, for example [60, 300, 900], or set it to [0] to turn second-level redelivery off for the {ProviderName} transport."));
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
