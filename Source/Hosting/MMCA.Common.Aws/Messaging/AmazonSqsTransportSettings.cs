namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The Amazon SQS/SNS transport's own keys, bound from <c>MessageBus:AmazonSqs</c>. Everything the
/// transports share (retry, redelivery, backpressure, endpoint naming) stays on the
/// <c>MessageBus</c> section itself.
/// </summary>
public sealed class AmazonSqsTransportSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "MessageBus:AmazonSqs";

    /// <summary>
    /// The AWS region (system name, e.g. <c>us-east-1</c>). When blank the AWS SDK fallback chain
    /// supplies it (<c>AWS_REGION</c>, the shared profile, instance metadata); with no region at all
    /// registration fails naming this key.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Optional endpoint override for both SQS and SNS, e.g. a LocalStack URL. Leave blank for AWS.
    /// Only an absolute URL counts.
    /// </summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>
    /// Optional static access key, set together with <see cref="SecretAccessKey"/>. When both are
    /// blank the AWS default credential chain applies (an IAM role, the environment, the shared
    /// profile). Setting only one of the two fails registration.
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>The secret paired with <see cref="AccessKeyId"/>.</summary>
    public string? SecretAccessKey { get; init; }
}
