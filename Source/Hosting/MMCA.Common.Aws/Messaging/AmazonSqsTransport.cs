using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The Amazon SQS/SNS broker transport (<c>MessageBus:Provider=AmazonSqs</c>), registered by
/// <c>AddAmazonSqsTransport()</c> and selected by <c>AddBrokerMessaging</c>, on the AWS SDK SQS and SNS
/// clients alone.
/// <para>
/// Topology: an SNS topic per event (<see cref="AmazonSqsEntityNames.Topic"/>), the host's SQS queue
/// (<see cref="AmazonSqsEntityNames.Queue"/>) subscribed to each consumed topic with raw message
/// delivery, a queue policy allowing exactly those topics to send to it, and a
/// <c>{queue}-error</c> queue (<see cref="AmazonSqsEntityNames.ErrorQueue"/>) for dead letters. With
/// <c>MessageBus:ProvisionTopology</c> on (the default) the receiver creates them idempotently when it
/// starts and the publisher creates a missing topic on its first publish
/// (<see cref="AmazonSqsTopologyProvisioner"/>). Every header travels packed in one message attribute
/// (<see cref="AmazonSqsMessageMapping"/>), so the 10-attribute cap never applies.
/// </para>
/// <para>
/// Redelivery sends a copy to the queue with <c>DelaySeconds</c>. SQS caps a message delay at 15
/// minutes, which is what <see cref="MaxRedeliveryDelay"/> declares: the framework's receive pipeline
/// shortens every second-level redelivery interval to it.
/// </para>
/// <para>
/// Configuration (<c>MessageBus:AmazonSqs</c>, <see cref="AmazonSqsTransportSettings"/>): <c>Region</c>
/// (else the AWS SDK fallback chain, else registration fails naming the key), <c>ServiceUrl</c> (one
/// endpoint override for both SQS and SNS, e.g. LocalStack) and <c>AccessKeyId</c> +
/// <c>SecretAccessKey</c> (both or neither; neither uses the AWS default credential chain). All of it is
/// validated in <see cref="Register"/>, while <c>AddBrokerMessaging</c> runs.
/// </para>
/// </summary>
internal sealed class AmazonSqsTransport : IBrokerTransport
{
    /// <summary>The <c>MessageBus:Provider</c> value that selects this transport.</summary>
    internal const string ProviderName = "AmazonSqs";

    /// <summary>The longest delay SQS accepts on a message, in seconds (15 minutes).</summary>
    internal const int MaxDelaySeconds = 900;

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
    public TimeSpan? MaxRedeliveryDelay => TimeSpan.FromSeconds(MaxDelaySeconds);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// No region is configured or resolvable, or only one half of the access key pair is set.
    /// </exception>
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        // Resolved HERE, while AddBrokerMessaging runs, so a configuration error fails startup rather
        // than the first publish.
        var sqsSettings = ReadSettings(context.Configuration);
        var region = ResolveRegion(sqsSettings.Region, _fallbackRegion);
        var credentials = AwsStaticCredentials.Resolve(
            sqsSettings.AccessKeyId, sqsSettings.SecretAccessKey, AmazonSqsTransportSettings.SectionName);
        var serviceUrl = sqsSettings.ServiceUrl is { IsAbsoluteUri: true } url ? url : null;
        var settings = context.Settings;
        var topology = context.Topology;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new AmazonSqsConnection(region, serviceUrl, credentials));

        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<AmazonSqsConnection>();
            return new AmazonSqsTopologyProvisioner(
                connection.Sqs, connection.Sns, settings.ProvisionTopology, LoggerFor<AmazonSqsTopologyProvisioner>(sp));
        });

        services.AddSingleton<IBrokerPublisher>(sp => new AmazonSqsPublisher(
            sp.GetRequiredService<AmazonSqsConnection>().Sns,
            sp.GetRequiredService<AmazonSqsTopologyProvisioner>()));

        services.AddHostedService(sp => new AmazonSqsReceiver(
            sp.GetRequiredService<AmazonSqsConnection>().Sqs,
            topology,
            new AmazonSqsDeliveryHandler(
                sp.GetRequiredService<IBrokerReceivePipeline>(),
                sp.GetRequiredService<TimeProvider>(),
                LoggerFor<AmazonSqsDeliveryHandler>(sp)),
            sp.GetRequiredService<AmazonSqsTopologyProvisioner>(),
            settings,
            LoggerFor<AmazonSqsReceiver>(sp)));
    }

    /// <summary>Binds <c>MessageBus:AmazonSqs</c>, or the defaults when the section is absent.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The bound settings.</returns>
    internal static AmazonSqsTransportSettings ReadSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetSection(AmazonSqsTransportSettings.SectionName).Get<AmazonSqsTransportSettings>()
            ?? new AmazonSqsTransportSettings();
    }

    /// <summary>
    /// Resolves the region for <paramref name="configuration"/>: <c>MessageBus:AmazonSqs:Region</c>,
    /// else this instance's fallback chain.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The region endpoint.</returns>
    /// <exception cref="InvalidOperationException">No region is configured or resolvable.</exception>
    internal RegionEndpoint ResolveRegion(IConfiguration configuration) =>
        ResolveRegion(ReadSettings(configuration).Region, _fallbackRegion);

    /// <summary>
    /// Resolves the region: the configured system name, else the AWS SDK fallback chain, else a
    /// registration failure naming the key, because the transport cannot address SQS without one.
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

    private static ILogger LoggerFor<T>(IServiceProvider services) =>
        (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<T>();
}
