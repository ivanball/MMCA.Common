using Amazon.SimpleNotificationService;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// The Amazon SQS/SNS <see cref="IBrokerPublisher"/>: publishes each message to its event's SNS topic,
/// whose ARN <see cref="AmazonSqsTopologyProvisioner"/> resolves once (creating a missing topic when
/// provisioning is on) and caches. Every subscribed queue receives a copy through raw message
/// delivery. A publish SNS rejects throws, so the outbox retries the row.
/// </summary>
internal sealed class AmazonSqsPublisher : IBrokerPublisher
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly AmazonSqsTopologyProvisioner _topology;

    /// <summary>Initializes a new instance of the <see cref="AmazonSqsPublisher"/> class.</summary>
    /// <param name="sns">The SNS client.</param>
    /// <param name="topology">Resolves (and when provisioning, creates) each topic.</param>
    public AmazonSqsPublisher(IAmazonSimpleNotificationService sns, AmazonSqsTopologyProvisioner topology)
    {
        ArgumentNullException.ThrowIfNull(sns);
        ArgumentNullException.ThrowIfNull(topology);

        _sns = sns;
        _topology = topology;
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var topicArn = await _topology.GetTopicArnAsync(AmazonSqsEntityNames.Topic(message.Topic), cancellationToken).ConfigureAwait(false);

        await _sns.PublishAsync(AmazonSqsMessageMapping.ToPublishRequest(topicArn, message), cancellationToken).ConfigureAwait(false);
    }
}
