using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Aws.Messaging;

/// <summary>
/// Resolves, and with <c>MessageBus:ProvisionTopology</c> on (the default) creates idempotently, the SNS
/// and SQS entities a host's <see cref="BrokerTopology"/> needs: the host's queue and its
/// <c>{queue}-error</c> queue, an SNS topic per consumed event, each topic subscribed to the queue with
/// raw message delivery (so the SQS message carries the published body and attributes unchanged), and
/// a queue policy allowing <c>sns:SendMessage</c> from exactly those topics. The publisher asks it for
/// each topic's ARN: created on first use when provisioning (SNS <c>CreateTopic</c> is idempotent),
/// looked up otherwise, and cached for the life of the process either way, so a steady-state publish
/// costs no management call.
/// </summary>
internal sealed partial class AmazonSqsTopologyProvisioner
{
    /// <summary>How long a dead-lettered message stays in the error queue: 14 days, the SQS maximum.</summary>
    internal const int ErrorQueueRetentionSeconds = 1_209_600;

    private readonly IAmazonSQS _sqs;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly bool _provision;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _topicArns = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="AmazonSqsTopologyProvisioner"/> class.</summary>
    /// <param name="sqs">The SQS client.</param>
    /// <param name="sns">The SNS client.</param>
    /// <param name="provision">Whether to create missing entities (<c>MessageBus:ProvisionTopology</c>) or only look them up.</param>
    /// <param name="logger">The logger provisioning is reported to.</param>
    public AmazonSqsTopologyProvisioner(IAmazonSQS sqs, IAmazonSimpleNotificationService sns, bool provision, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(sns);
        ArgumentNullException.ThrowIfNull(logger);

        _sqs = sqs;
        _sns = sns;
        _provision = provision;
        _logger = logger;
    }

    /// <summary>
    /// The ARN of an SNS topic: created when provisioning, otherwise looked up, then cached.
    /// </summary>
    /// <param name="topicName">The SNS topic name (<see cref="AmazonSqsEntityNames.Topic"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The topic ARN.</returns>
    /// <exception cref="InvalidOperationException">Provisioning is off and the topic does not exist.</exception>
    public async Task<string> GetTopicArnAsync(string topicName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        if (_topicArns.TryGetValue(topicName, out var cached))
        {
            return cached;
        }

        string arn;
        if (_provision)
        {
            arn = (await _sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }, cancellationToken).ConfigureAwait(false)).TopicArn;
        }
        else
        {
            var topic = await _sns.FindTopicAsync(topicName).ConfigureAwait(false);
            arn = topic?.TopicArn ?? throw new InvalidOperationException(
                $"The SNS topic '{topicName}' does not exist and MessageBus:ProvisionTopology is false. Create it, or turn provisioning on.");
        }

        return _topicArns.GetOrAdd(topicName, arn);
    }

    /// <summary>
    /// Makes the host's queues ready and returns their URLs. When provisioning: the error queue, the
    /// queue, every consumed topic, the queue policy for those topics, then one raw-delivery subscription
    /// per topic (the policy first, so no message a subscription delivers is refused). Otherwise only the
    /// two queue URLs are looked up.
    /// </summary>
    /// <param name="topology">The host's topology.</param>
    /// <param name="cancellationToken">Cancels provisioning.</param>
    /// <returns>The URLs of the host's queue and its error queue.</returns>
    public async Task<(string QueueUrl, string ErrorQueueUrl)> ProvisionAsync(BrokerTopology topology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var queueName = AmazonSqsEntityNames.Queue(topology.QueueName);
        var errorQueueName = AmazonSqsEntityNames.ErrorQueue(topology.QueueName);

        if (!_provision)
        {
            var queue = await _sqs.GetQueueUrlAsync(queueName, cancellationToken).ConfigureAwait(false);
            var errorQueue = await _sqs.GetQueueUrlAsync(errorQueueName, cancellationToken).ConfigureAwait(false);
            return (queue.QueueUrl, errorQueue.QueueUrl);
        }

        var errorQueueUrl = await EnsureQueueAsync(errorQueueName, ErrorQueueAttributes(), cancellationToken).ConfigureAwait(false);
        var queueUrl = await EnsureQueueAsync(queueName, QueueAttributes(), cancellationToken).ConfigureAwait(false);

        var queueAttributes = await _sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = [QueueAttributeName.QueueArn] },
            cancellationToken).ConfigureAwait(false);
        var queueArn = queueAttributes.QueueARN;

        var topicArns = new List<string>();
        foreach (var topic in topology.Subscriptions.Select(s => AmazonSqsEntityNames.Topic(s.Topic)).Distinct(StringComparer.Ordinal))
        {
            topicArns.Add(await GetTopicArnAsync(topic, cancellationToken).ConfigureAwait(false));
        }

        await _sqs.SetQueueAttributesAsync(
            new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { [QueueAttributeName.Policy] = BuildQueuePolicy(queueArn, topicArns) },
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var topicArn in topicArns)
        {
            await EnsureRawSubscriptionAsync(topicArn, queueArn, cancellationToken).ConfigureAwait(false);
        }

        LogProvisioned(_logger, queueName, errorQueueName, topicArns.Count);
        return (queueUrl, errorQueueUrl);
    }

    /// <summary>
    /// The queue policy that lets SNS deliver to <paramref name="queueArn"/> from exactly
    /// <paramref name="topicArns"/>, and from nothing else.
    /// </summary>
    /// <param name="queueArn">The host's queue.</param>
    /// <param name="topicArns">The topics subscribed to it.</param>
    /// <returns>The policy document (JSON).</returns>
    internal static string BuildQueuePolicy(string queueArn, IReadOnlyCollection<string> topicArns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueArn);
        ArgumentNullException.ThrowIfNull(topicArns);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Version", "2012-10-17");
            writer.WriteStartArray("Statement");
            writer.WriteStartObject();
            writer.WriteString("Sid", "mmca-sns-topics");
            writer.WriteString("Effect", "Allow");
            writer.WriteStartObject("Principal");
            writer.WriteString("Service", "sns.amazonaws.com");
            writer.WriteEndObject();
            writer.WriteString("Action", "sqs:SendMessage");
            writer.WriteString("Resource", queueArn);
            writer.WriteStartObject("Condition");
            writer.WriteStartObject("ArnEquals");
            writer.WriteStartArray("aws:SourceArn");
            foreach (var topicArn in topicArns)
            {
                writer.WriteStringValue(topicArn);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// The attributes the host's queue is created with: the visibility timeout the heartbeat renews
    /// (each receive asks for it as well, so a queue created elsewhere behaves the same).
    /// </summary>
    /// <returns>The attributes.</returns>
    internal static Dictionary<string, string> QueueAttributes() => new(StringComparer.Ordinal)
    {
        [QueueAttributeName.VisibilityTimeout] = AmazonSqsDeliveryHandler.VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The attributes the error queue is created with: the longest retention SQS allows.</summary>
    /// <returns>The attributes.</returns>
    internal static Dictionary<string, string> ErrorQueueAttributes() => new(StringComparer.Ordinal)
    {
        [QueueAttributeName.MessageRetentionPeriod] = ErrorQueueRetentionSeconds.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Creates a queue, or returns the URL of the existing one: <c>CreateQueue</c> is idempotent when the
    /// attributes match, and a queue someone created with other attributes is left exactly as it is.
    /// </summary>
    private async Task<string> EnsureQueueAsync(string queueName, Dictionary<string, string> attributes, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Attributes = attributes }, cancellationToken).ConfigureAwait(false);
            return created.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            return (await _sqs.GetQueueUrlAsync(queueName, cancellationToken).ConfigureAwait(false)).QueueUrl;
        }
    }

    /// <summary>
    /// Subscribes the queue to a topic with raw message delivery. <c>Subscribe</c> is idempotent for an
    /// identical subscription; one that exists with other attributes is switched to raw delivery instead.
    /// </summary>
    private async Task EnsureRawSubscriptionAsync(string topicArn, string queueArn, CancellationToken cancellationToken)
    {
        try
        {
            await _sns.SubscribeAsync(
                new SubscribeRequest
                {
                    TopicArn = topicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn,
                    ReturnSubscriptionArn = true,
                    Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["RawMessageDelivery"] = "true" },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidParameterException)
        {
            var existing = await FindSubscriptionArnAsync(topicArn, queueArn, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            await _sns.SetSubscriptionAttributesAsync(existing, "RawMessageDelivery", "true", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> FindSubscriptionArnAsync(string topicArn, string queueArn, CancellationToken cancellationToken)
    {
        string? nextToken = null;
        do
        {
            var page = await _sns.ListSubscriptionsByTopicAsync(
                new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = nextToken },
                cancellationToken).ConfigureAwait(false);

            var match = page.Subscriptions?.FirstOrDefault(s =>
                string.Equals(s.Protocol, "sqs", StringComparison.Ordinal) && string.Equals(s.Endpoint, queueArn, StringComparison.Ordinal));
            if (match is not null)
            {
                return match.SubscriptionArn;
            }

            nextToken = page.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Provisioned the Amazon SQS queue '{Queue}' (error queue '{ErrorQueue}') and its subscriptions to {TopicCount} SNS topics.")]
    private static partial void LogProvisioned(ILogger logger, string queue, string errorQueue, int topicCount);
}
