using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The SQS and SNS clients the transport shares, built once per host from the settings
/// <see cref="AmazonSqsTransport"/> validated at registration: the resolved region, the optional
/// endpoint override (LocalStack) applied to both, and static credentials or the AWS default chain.
/// </summary>
internal sealed class AmazonSqsConnection : IDisposable
{
    private readonly AmazonSQSClient _sqs;
    private readonly AmazonSimpleNotificationServiceClient _sns;

    /// <summary>Initializes a new instance of the <see cref="AmazonSqsConnection"/> class.</summary>
    /// <param name="region">The resolved region (the signing region when <paramref name="serviceUrl"/> is set).</param>
    /// <param name="serviceUrl">The endpoint override for both services, or <see langword="null"/> for AWS.</param>
    /// <param name="credentials">Static credentials, or <see langword="null"/> for the AWS default credential chain.</param>
    public AmazonSqsConnection(RegionEndpoint region, Uri? serviceUrl, AWSCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(region);

        var sqsConfig = CreateSqsConfig(region, serviceUrl);
        var snsConfig = CreateSnsConfig(region, serviceUrl);

        _sqs = credentials is null ? new AmazonSQSClient(sqsConfig) : new AmazonSQSClient(credentials, sqsConfig);
        _sns = credentials is null
            ? new AmazonSimpleNotificationServiceClient(snsConfig)
            : new AmazonSimpleNotificationServiceClient(credentials, snsConfig);
    }

    /// <summary>Gets the SQS client.</summary>
    public IAmazonSQS Sqs => _sqs;

    /// <summary>Gets the SNS client.</summary>
    public IAmazonSimpleNotificationService Sns => _sns;

    /// <summary>The SQS client configuration for the region and the optional endpoint override.</summary>
    /// <param name="region">The resolved region.</param>
    /// <param name="serviceUrl">The endpoint override, or <see langword="null"/>.</param>
    /// <returns>The configuration.</returns>
    internal static AmazonSQSConfig CreateSqsConfig(RegionEndpoint region, Uri? serviceUrl)
    {
        ArgumentNullException.ThrowIfNull(region);

        var config = new AmazonSQSConfig();
        AwsClientEndpoint.Apply(config, serviceUrl, region.SystemName);
        return config;
    }

    /// <summary>The SNS client configuration for the region and the optional endpoint override.</summary>
    /// <param name="region">The resolved region.</param>
    /// <param name="serviceUrl">The endpoint override, or <see langword="null"/>.</param>
    /// <returns>The configuration.</returns>
    internal static AmazonSimpleNotificationServiceConfig CreateSnsConfig(RegionEndpoint region, Uri? serviceUrl)
    {
        ArgumentNullException.ThrowIfNull(region);

        var config = new AmazonSimpleNotificationServiceConfig();
        AwsClientEndpoint.Apply(config, serviceUrl, region.SystemName);
        return config;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sqs.Dispose();
        _sns.Dispose();
    }
}
