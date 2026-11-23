using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The Amazon SQS/SNS broker transport (<c>MessageBus:Provider=AmazonSqs</c>), registered by
/// <c>AddAmazonSqsTransport()</c> and selected by <c>AddBrokerMessaging</c>: one SNS topic per event,
/// the host's SQS queue subscribed to each with raw delivery.
/// <para>
/// SQS caps a message delay at 15 minutes, which is what <see cref="MaxRedeliveryDelay"/> declares:
/// the framework's receive pipeline shortens every second-level redelivery interval to it.
/// </para>
/// <para>
/// PLACEHOLDER on <c>feat/native-messaging</c>: the native AWS SDK implementation (publish to SNS,
/// receive from SQS, <c>SendMessage</c> with <c>DelaySeconds</c> for redelivery and a
/// <c>{queue}-error</c> queue) replaces <see cref="Register"/> before the branch merges. The region
/// resolution below is kept for it.
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
    public void Register(IServiceCollection services, BrokerTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        throw new NotSupportedException("Amazon SQS transport: native implementation pending on feat/native-messaging");
    }

    /// <summary>
    /// Resolves the region for <paramref name="configuration"/>: <c>MessageBus:AmazonSqs:Region</c>,
    /// else this instance's fallback chain.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The region endpoint.</returns>
    /// <exception cref="InvalidOperationException">No region is configured or resolvable.</exception>
    internal RegionEndpoint ResolveRegion(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration.GetSection(AmazonSqsTransportSettings.SectionName).Get<AmazonSqsTransportSettings>()
            ?? new AmazonSqsTransportSettings();

        return ResolveRegion(settings.Region, _fallbackRegion);
    }

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
}
