using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Infrastructure.Messaging.Consumers;

/// <summary>
/// The framework's one <see cref="IBrokerReceivePipeline"/>, shared by every transport. Per delivered
/// message it restores the publisher's trace context as the parent of a Consumer span, resolves the
/// subscription from <see cref="BrokerHeaders.EventName"/>, deserializes the body, and runs the
/// subscription's consumer (inbox, origin restore, handlers) with in-process exponential retry, each
/// attempt in a FRESH DI scope. When every retry fails it asks the transport for a second-level
/// redelivery on the host's queue, and once those are spent too it dead-letters the message, logging
/// one Error and counting <c>broker.fault.count</c>.
/// <para>
/// It never throws for a handler or payload failure. The only exception it lets out is the
/// <see cref="OperationCanceledException"/> of the host's own token, and the transport then leaves the
/// message unacknowledged so the broker delivers it again after the restart.
/// </para>
/// </summary>
internal sealed partial class BrokerReceivePipeline : IBrokerReceivePipeline
{
    /// <summary>The dead-letter reason for a delivery whose event name this host does not consume.</summary>
    internal const string UnknownEventReason = "unknown-event";

    /// <summary>The dead-letter reason for a body that does not deserialize to the subscription's type.</summary>
    internal const string PoisonReason = "poison";

    /// <summary>The dead-letter reason for a message whose handlers failed every retry and redelivery.</summary>
    internal const string HandlerFailedReason = "handler-failed";

    /// <summary>The longest dead-letter detail written; brokers cap the description they store.</summary>
    private const int MaxDetailLength = 1024;

    private readonly BrokerTopology _topology;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MessageBusSettings _settings;
    private readonly TimeSpan[] _redeliveryIntervals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BrokerReceivePipeline> _logger;
    private readonly Dictionary<string, Type> _consumerTypes;

    /// <summary>Initializes a new instance of the <see cref="BrokerReceivePipeline"/> class.</summary>
    /// <param name="topology">The host's topology.</param>
    /// <param name="scopeFactory">Creates the fresh scope of each attempt.</param>
    /// <param name="settings">The bound <c>MessageBus</c> section (retry and redelivery policy).</param>
    /// <param name="maxRedeliveryDelay">The selected transport's redelivery delay cap, or <see langword="null"/>.</param>
    /// <param name="timeProvider">The clock retry delays wait on.</param>
    /// <param name="logger">The pipeline's logger.</param>
    public BrokerReceivePipeline(
        BrokerTopology topology,
        IServiceScopeFactory scopeFactory,
        MessageBusSettings settings,
        TimeSpan? maxRedeliveryDelay,
        TimeProvider timeProvider,
        ILogger<BrokerReceivePipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _topology = topology;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _redeliveryIntervals = BrokerTransportPolicies.BuildRedeliveryIntervals(settings, maxRedeliveryDelay);
        _timeProvider = timeProvider;
        _logger = logger;
        _consumerTypes = topology.Subscriptions.ToDictionary(
            s => s.EventName,
            BrokerConsumerBuilder.ConsumerTypeFor,
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var activity = StartConsumerActivity(message);

        if (!TryResolve(message, out var subscription, out var integrationEvent, out var rejection))
        {
            activity?.SetStatus(ActivityStatusCode.Error, rejection.Reason);
            return rejection;
        }

        activity?.SetTag("mmca.event.name", subscription.EventName);

        var failure = await ConsumeWithRetryAsync(_consumerTypes[subscription.EventName], integrationEvent, message.Headers, cancellationToken)
            .ConfigureAwait(false);
        if (failure is null)
        {
            return BrokerDisposition.Complete;
        }

        activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
        return AfterRetriesExhausted(message.RedeliveryCount, subscription, integrationEvent, failure);
    }

    /// <summary>
    /// Resolves the subscription from the event-name header and deserializes the body. A delivery
    /// that fails either step is rejected with the dead-letter disposition: no retry can fix it.
    /// </summary>
    private bool TryResolve(
        IncomingBrokerMessage message,
        [NotNullWhen(true)] out BrokerSubscription? subscription,
        [NotNullWhen(true)] out IIntegrationEvent? integrationEvent,
        [NotNullWhen(false)] out BrokerDisposition.DeadLetter? rejection)
    {
        integrationEvent = null;

        var eventName = message.EventName;
        if (eventName is null || !_topology.TryGetSubscription(eventName, out subscription))
        {
            subscription = null;
            LogUnknownEvent(_logger, eventName ?? "<missing>", _topology.QueueName, message.BrokerMessageId);
            rejection = new BrokerDisposition.DeadLetter(
                UnknownEventReason,
                $"The host consuming '{_topology.QueueName}' has no subscription for event '{eventName ?? "<missing " + BrokerHeaders.EventName + " header>"}'.");
            return false;
        }

        try
        {
            integrationEvent = JsonSerializer.Deserialize(message.Body.Span, subscription.EventType, BrokerWire.JsonOptions) as IIntegrationEvent;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            LogPoison(_logger, ex, subscription.EventType.Name, _topology.QueueName, message.BrokerMessageId);
            rejection = new BrokerDisposition.DeadLetter(PoisonReason, Truncate(ex.Message));
            return false;
        }

        if (integrationEvent is null)
        {
            LogPoison(_logger, null, subscription.EventType.Name, _topology.QueueName, message.BrokerMessageId);
            rejection = new BrokerDisposition.DeadLetter(PoisonReason, $"The body is not a {subscription.EventType.Name}.");
            return false;
        }

        rejection = null;
        return true;
    }

    /// <summary>
    /// The disposition once every in-process retry failed: the next second-level redelivery while one
    /// is left, otherwise a dead-letter with one Error log and one <c>broker.fault.count</c>.
    /// </summary>
    private BrokerDisposition AfterRetriesExhausted(
        int redeliveryCount,
        BrokerSubscription subscription,
        IIntegrationEvent integrationEvent,
        Exception failure)
    {
        if (redeliveryCount < _redeliveryIntervals.Length)
        {
            var delay = _redeliveryIntervals[redeliveryCount];
            LogRedelivering(_logger, subscription.EventType.Name, integrationEvent.MessageId, redeliveryCount + 1, _redeliveryIntervals.Length, delay);
            return new BrokerDisposition.Redeliver(delay, redeliveryCount + 1);
        }

        var detail = Describe(failure);
        LogDeadLettered(_logger, failure, subscription.EventType.Name, integrationEvent.MessageId, _topology.QueueName, detail);
        BrokerMetrics.FaultCounter.Add(
            1,
            new KeyValuePair<string, object?>("event_type", subscription.EventType.Name),
            new KeyValuePair<string, object?>("queue", _topology.QueueName));

        return new BrokerDisposition.DeadLetter(HandlerFailedReason, detail);
    }

    /// <summary>
    /// Runs the consumer up to <see cref="MessageBusSettings.RetryLimit"/> + 1 times, each attempt in a
    /// fresh scope, waiting <see cref="BrokerTransportPolicies.RetryDelay"/> between attempts.
    /// </summary>
    /// <returns><see langword="null"/> on success; otherwise the last attempt's exception.</returns>
    private async Task<Exception?> ConsumeWithRetryAsync(
        Type consumerType,
        IIntegrationEvent integrationEvent,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var retryLimit = Math.Max(0, _settings.RetryLimit);
        Exception? failure = null;

        for (var attempt = 0; attempt <= retryLimit; attempt++)
        {
            if (attempt > 0)
            {
                var delay = BrokerTransportPolicies.RetryDelay(_settings, attempt);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ConsumeOnceAsync(consumerType, integrationEvent, headers, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A cancellation of the host's own token is not a failure and propagates (the filter
                // lets it through); anything else, including a handler's own timeout, is retried.
                failure = ex;
                if (attempt < retryLimit)
                {
                    LogAttemptFailed(_logger, ex, integrationEvent.GetType().Name, integrationEvent.MessageId, attempt + 1, retryLimit + 1);
                }
            }
        }

        return failure;
    }

    private async Task ConsumeOnceAsync(
        Type consumerType,
        IIntegrationEvent integrationEvent,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var consumer = (IBrokerMessageConsumer)scope.ServiceProvider.GetRequiredService(consumerType);
            await consumer.ConsumeAsync(integrationEvent, headers, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the Consumer span, parented on the publisher's W3C context when the message carries one,
    /// so the sampling decision travels with it: an unsampled publisher yields an unsampled consumer
    /// span under a parent-based sampler.
    /// </summary>
    private Activity? StartConsumerActivity(IncomingBrokerMessage message)
    {
        ActivityContext parent = default;
        if (message.Headers.TryGetValue(BrokerHeaders.TraceParent, out var traceParent))
        {
            message.Headers.TryGetValue(BrokerHeaders.TraceState, out var traceState);
            if (!ActivityContext.TryParse(traceParent, traceState, isRemote: true, out parent))
            {
                parent = default;
            }
        }

        var activity = BrokerWire.ActivitySource.StartActivity(_topology.QueueName + " process", ActivityKind.Consumer, parent);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", BrokerWire.MessagingSystem);
            activity.SetTag("messaging.operation.type", "process");
            activity.SetTag("messaging.destination.name", _topology.QueueName);
            if (message.Headers.TryGetValue(BrokerHeaders.MessageId, out var messageId))
            {
                activity.SetTag("messaging.message.id", messageId);
            }
        }

        return activity;
    }

    /// <summary>Every exception message in the chain, innermost cause included, on one line.</summary>
    private static string Describe(Exception failure)
    {
        List<string> messages = [];
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            messages.Add(current.GetType().Name + ": " + current.Message);
        }

        return Truncate(string.Join(" | ", messages));
    }

    private static string Truncate(string value) =>
        value.Length <= MaxDetailLength ? value : value[..MaxDetailLength];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker message {BrokerMessageId} on queue {Queue} carries event '{EventName}', which this host does not consume; dead-lettered")]
    private static partial void LogUnknownEvent(ILogger logger, string eventName, string queue, string brokerMessageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker message {BrokerMessageId} on queue {Queue} does not deserialize to {EventType}; dead-lettered as poison")]
    private static partial void LogPoison(ILogger logger, Exception? ex, string eventType, string queue, string brokerMessageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration event {EventType} (message {MessageId}) failed attempt {Attempt} of {Attempts}; retrying in a fresh scope")]
    private static partial void LogAttemptFailed(ILogger logger, Exception ex, string eventType, Guid messageId, int attempt, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration event {EventType} (message {MessageId}) exhausted its in-process retries; scheduling redelivery {Redelivery} of {Redeliveries} in {Delay}")]
    private static partial void LogRedelivering(ILogger logger, string eventType, Guid messageId, int redelivery, int redeliveries, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration event {EventType} (message {MessageId}) exhausted its retries and redeliveries and was dead-lettered from queue {Queue}: {Reasons}")]
    private static partial void LogDeadLettered(ILogger logger, Exception ex, string eventType, Guid messageId, string queue, string reasons);
}
