using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Tests.Messaging.Broker;
using MMCA.Common.Infrastructure.Tests.TestDoubles;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Consumers;

/// <summary>
/// The framework's receive pipeline, driven end to end through <c>AddBrokerMessaging</c>, the
/// <see cref="InMemoryBrokerTransport"/> and a real <c>BrokerMessageBus</c> publish: completion, inbox
/// dedupe, in-process retry in fresh scopes, second-level redelivery (intervals, count and clamp),
/// dead-lettering with its log and metric, poison and unknown events, cancellation, and the trace
/// parent carried across the hop.
/// </summary>
public sealed class BrokerReceivePipelineTests
{
    public sealed record class PipelineEvent(string Sku) : BaseIntegrationEvent;

    public sealed record class NotSubscribedEvent : BaseIntegrationEvent;

    [Fact]
    public async Task AHandlerThatSucceeds_Completes()
    {
        var harness = Harness.Create();
        var message = await harness.PublishAsync(new PipelineEvent("sku-1"));

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        dispositions.Should().ContainSingle().Which.Should().Be(BrokerDisposition.Complete);
        harness.Handler.Received.Should().ContainSingle().Which.Sku.Should().Be("sku-1");
        harness.Inbox.Completed.Should().ContainSingle();
    }

    [Fact]
    public async Task AMessageTheInboxAlreadyRecorded_IsCompletedWithoutRunningTheHandlers()
    {
        var harness = Harness.Create();
        var evt = new PipelineEvent("dup");
        harness.Inbox.Completed[evt.MessageId] = true;
        var message = await harness.PublishAsync(evt);

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        dispositions.Should().ContainSingle().Which.Should().Be(BrokerDisposition.Complete);
        harness.Handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AHandlerThatFailsThenSucceeds_IsRetriedInAFreshScopePerAttempt()
    {
        var harness = Harness.Create(retryLimit: 3, failures: 2);
        var message = await harness.PublishAsync(new PipelineEvent("flaky"));

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        dispositions.Should().ContainSingle().Which.Should().Be(BrokerDisposition.Complete);
        harness.Handler.Calls.Should().Be(3);
        harness.ScopeCount.Should().Be(3, "every attempt resolves its consumer from a fresh scope");
    }

    [Fact]
    public async Task RetryDelays_WaitOnTheInjectedTimeProvider()
    {
        var clock = new FakeTimeProvider();
        var harness = Harness.Create(retryLimit: 1, failures: 1, retryMinSeconds: 5, timeProvider: clock);
        var message = await harness.PublishAsync(new PipelineEvent("timed"));

        var processing = harness.Pipeline.ProcessAsync(
            new IncomingBrokerMessage(message.Body, message.Headers, "b-1"),
            TestContext.Current.CancellationToken);

        // The retry waits five seconds of the FAKE clock: advance until it fires.
        for (var i = 0; i < 200 && !processing.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        (await processing).Should().Be(BrokerDisposition.Complete);
        harness.Handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task RetriesExhausted_RedeliversWithTheNextIntervalAndCount_ThenDeadLetters()
    {
        var harness = Harness.Create(retryLimit: 1, failures: int.MaxValue, redeliveryIntervals: ["5", "15"]);
        var message = await harness.PublishAsync(new PipelineEvent("down"));

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        dispositions.Should().HaveCount(3);
        dispositions[0].Should().Be(new BrokerDisposition.Redeliver(TimeSpan.FromSeconds(5), 1));
        dispositions[1].Should().Be(new BrokerDisposition.Redeliver(TimeSpan.FromSeconds(15), 2));
        dispositions[2].Should().BeOfType<BrokerDisposition.DeadLetter>()
            .Which.Reason.Should().Be(BrokerReceivePipeline.HandlerFailedReason);
        harness.Handler.Calls.Should().Be(6, "two attempts (one retry) per delivery, three deliveries");
    }

    [Fact]
    public async Task RedeliveryIntervals_AreClampedToTheTransportsMaximumDelay()
    {
        var harness = Harness.Create(retryLimit: 0, failures: int.MaxValue, maxRedeliveryDelay: TimeSpan.FromSeconds(30));
        var message = await harness.PublishAsync(new PipelineEvent("clamped"));

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        dispositions.OfType<BrokerDisposition.Redeliver>().Select(r => r.Delay)
            .Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        dispositions[^1].Should().BeOfType<BrokerDisposition.DeadLetter>();
    }

    [Fact]
    public async Task ANonPositiveRedeliveryInterval_TurnsRedeliveryOff()
    {
        var harness = Harness.Create(retryLimit: 0, failures: int.MaxValue, redeliveryIntervals: ["0"]);
        var message = await harness.PublishAsync(new PipelineEvent("no-redelivery"));

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        dispositions.Should().ContainSingle().Which.Should().BeOfType<BrokerDisposition.DeadLetter>();
    }

    [Fact]
    public async Task DeadLettering_LogsOneErrorAndCountsTheFault()
    {
        var harness = Harness.Create(retryLimit: 0, failures: int.MaxValue, redeliveryIntervals: ["0"]);
        var message = await harness.PublishAsync(new PipelineEvent("dead"));
        List<(long Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = FaultListener(harness.QueueName, measurements);

        var dispositions = await InMemoryBroker.DeliverAsync(harness.Pipeline, message, TestContext.Current.CancellationToken);

        var deadLetter = dispositions.Should().ContainSingle().Which.Should().BeOfType<BrokerDisposition.DeadLetter>().Subject;
        deadLetter.Detail.Should().Contain("handler down");
        harness.PipelineLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(1);
        measurements[0].Tags["event_type"].Should().Be(nameof(PipelineEvent));
        measurements[0].Tags["queue"].Should().Be(harness.QueueName);
    }

    [Fact]
    public async Task ARedelivery_IsNotCountedAsAFault()
    {
        var harness = Harness.Create(retryLimit: 0, failures: int.MaxValue, redeliveryIntervals: ["5"]);
        var message = await harness.PublishAsync(new PipelineEvent("once"));
        List<(long Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = FaultListener(harness.QueueName, measurements);

        var first = await harness.Pipeline.ProcessAsync(
            new IncomingBrokerMessage(message.Body, message.Headers, "b-1"),
            TestContext.Current.CancellationToken);

        first.Should().BeOfType<BrokerDisposition.Redeliver>();
        measurements.Should().BeEmpty();
        harness.PipelineLog.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task AnUndeserializableBody_IsDeadLetteredAsPoison_WithoutRetry(string body)
    {
        var harness = Harness.Create(retryLimit: 5);
        var message = await harness.PublishAsync(new PipelineEvent("poison"));

        var disposition = await harness.Pipeline.ProcessAsync(
            new IncomingBrokerMessage(Encoding.UTF8.GetBytes(body), message.Headers, "b-1"),
            TestContext.Current.CancellationToken);

        disposition.Should().BeOfType<BrokerDisposition.DeadLetter>()
            .Which.Reason.Should().Be(BrokerReceivePipeline.PoisonReason);
        harness.Handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnEventThisHostDoesNotConsume_IsDeadLetteredAsUnknown()
    {
        var harness = Harness.Create();
        var message = await harness.PublishAsync(new NotSubscribedEvent());

        var disposition = await harness.Pipeline.ProcessAsync(
            new IncomingBrokerMessage(message.Body, message.Headers, "b-1"),
            TestContext.Current.CancellationToken);

        disposition.Should().BeOfType<BrokerDisposition.DeadLetter>()
            .Which.Reason.Should().Be(BrokerReceivePipeline.UnknownEventReason);
    }

    [Fact]
    public async Task AMessageWithNoEventNameHeader_IsDeadLetteredAsUnknown()
    {
        var harness = Harness.Create();

        var disposition = await harness.Pipeline.ProcessAsync(
            new IncomingBrokerMessage(Encoding.UTF8.GetBytes("{}"), new Dictionary<string, string>(), "b-1"),
            TestContext.Current.CancellationToken);

        disposition.Should().BeOfType<BrokerDisposition.DeadLetter>()
            .Which.Detail.Should().Contain(BrokerHeaders.EventName);
    }

    [Fact]
    public async Task HostCancellation_PropagatesInsteadOfCountingAsAFailure()
    {
        using var cts = new CancellationTokenSource();
        var harness = Harness.Create(retryLimit: 5, onHandle: () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        });
        var message = await harness.PublishAsync(new PipelineEvent("stopping"));

        var act = () => harness.Pipeline.ProcessAsync(new IncomingBrokerMessage(message.Body, message.Headers, "b-1"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Handler.Calls.Should().Be(1, "a stopping host is not retried");
    }

    [Fact]
    public async Task AHandlersOwnCancellation_WithTheHostRunning_IsAFailureLikeAnyOther()
    {
        var harness = Harness.Create(retryLimit: 1, redeliveryIntervals: ["0"], onHandle: () => throw new OperationCanceledException("handler timeout"));
        var message = await harness.PublishAsync(new PipelineEvent("timeout"));

        var disposition = await harness.Pipeline.ProcessAsync(
            new IncomingBrokerMessage(message.Body, message.Headers, "b-1"),
            TestContext.Current.CancellationToken);

        disposition.Should().BeOfType<BrokerDisposition.DeadLetter>();
        harness.Handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ASampledTraceparent_ParentsARecordedConsumerSpan()
    {
        var harness = Harness.Create();
        using var listener = ParentBasedListener();
        var traceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var message = await harness.PublishAsync(new PipelineEvent("traced"));
        var headers = WithTraceParent(message.Headers, $"00-{traceId}-{parentSpanId}-01");

        await harness.Pipeline.ProcessAsync(new IncomingBrokerMessage(message.Body, headers, "b-1"), TestContext.Current.CancellationToken);

        var consumerSpan = harness.Handler.Activities.Should().ContainSingle().Subject;
        consumerSpan.Should().NotBeNull();
        consumerSpan!.Kind.Should().Be(ActivityKind.Consumer);
        consumerSpan.TraceId.Should().Be(traceId);
        consumerSpan.ParentSpanId.Should().Be(parentSpanId);
        consumerSpan.Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnsampledTraceparent_YieldsAnUnrecordedConsumerSpan()
    {
        var harness = Harness.Create();
        using var listener = ParentBasedListener();
        var traceId = ActivityTraceId.CreateRandom();
        var message = await harness.PublishAsync(new PipelineEvent("unsampled"));
        var headers = WithTraceParent(message.Headers, $"00-{traceId}-{ActivitySpanId.CreateRandom()}-00");

        await harness.Pipeline.ProcessAsync(new IncomingBrokerMessage(message.Body, headers, "b-1"), TestContext.Current.CancellationToken);

        var consumerSpan = harness.Handler.Activities.Should().ContainSingle().Subject;
        consumerSpan.Should().NotBeNull("propagation data is still created, so the trace id flows on");
        consumerSpan!.TraceId.Should().Be(traceId);
        consumerSpan.Recorded.Should().BeFalse("an unsampled publisher must not produce a recorded consumer span");
        consumerSpan.IsAllDataRequested.Should().BeFalse();
    }

    private static Dictionary<string, string> WithTraceParent(IReadOnlyDictionary<string, string> headers, string traceParent) =>
        new(headers, StringComparer.Ordinal)
        {
            [BrokerHeaders.TraceParent] = traceParent,
        };

    /// <summary>A listener that samples like OpenTelemetry's default ParentBased(AlwaysOn) sampler.</summary>
    private static ActivityListener ParentBasedListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MMCA.Common.Messaging",
            Sample = static (ref options) =>
                options.Parent == default || options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener FaultListener(string queue, List<(long Value, Dictionary<string, object?> Tags)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "MMCA.Common.Broker" && instrument.Name == "broker.fault.count")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var tagMap = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                tagMap[tag.Key] = tag.Value;
            }

            // Other tests run in parallel against the same static meter; only this test's queue counts.
            if (Equals(tagMap.GetValueOrDefault("queue"), queue))
            {
                lock (measurements)
                {
                    measurements.Add((value, tagMap));
                }
            }
        });
        listener.Start();
        return listener;
    }

    /// <summary>A host wired through AddBrokerMessaging with the in-memory transport.</summary>
    private sealed class Harness
    {
        private int _scopeCount;

        private Harness()
        {
        }

        public required IBrokerReceivePipeline Pipeline { get; init; }

        public required IServiceProvider Services { get; init; }

        public required RecordingHandler Handler { get; init; }

        public required FakeInbox Inbox { get; init; }

        public required RecordingLogger<BrokerReceivePipeline> PipelineLog { get; init; }

        public required string QueueName { get; init; }

        public int ScopeCount => _scopeCount;

        public static Harness Create(
            int retryLimit = 0,
            int failures = 0,
            int retryMinSeconds = 0,
            string[]? redeliveryIntervals = null,
            TimeSpan? maxRedeliveryDelay = null,
            TimeProvider? timeProvider = null,
            Action? onHandle = null)
        {
            var queueName = "q-" + Guid.NewGuid().ToString("N")[..12];
            var settings = new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = InMemoryBrokerTransport.DefaultName,
                ["MessageBus:QueueName"] = queueName,
                ["MessageBus:RetryLimit"] = retryLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MessageBus:RetryMinIntervalSeconds"] = retryMinSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MessageBus:RetryMaxIntervalSeconds"] = "30",
                ["Application:Namespace"] = "pipeline-tests",
            };
            for (var i = 0; i < (redeliveryIntervals?.Length ?? 0); i++)
            {
                settings[string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MessageBus:RedeliveryIntervalsSeconds:{i}")] = redeliveryIntervals![i];
            }

            var handler = new RecordingHandler(failures, onHandle);
            var inbox = new FakeInbox();
            var pipelineLog = new RecordingLogger<BrokerReceivePipeline>();
            Harness? harness = null;

            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<ILogger<BrokerReceivePipeline>>(pipelineLog);
            services.AddSingleton<IIntegrationEventHandler<PipelineEvent>>(handler);
            services.AddScoped<IInboxStore>(_ =>
            {
                Interlocked.Increment(ref harness!._scopeCount);
                return inbox;
            });
            if (timeProvider is not null)
            {
                services.AddSingleton(timeProvider);
            }

            services.AddBrokerTransport(new InMemoryBrokerTransport(maxRedeliveryDelay: maxRedeliveryDelay));
            services.AddBrokerMessaging(
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                consumers => consumers.RegisterIntegrationEventConsumer<PipelineEvent>());

            var provider = services.BuildServiceProvider();
            harness = new Harness
            {
                Pipeline = provider.GetRequiredService<IBrokerReceivePipeline>(),
                Services = provider,
                Handler = handler,
                Inbox = inbox,
                PipelineLog = pipelineLog,
                QueueName = queueName,
            };
            return harness;
        }

        /// <summary>Publishes through the real BrokerMessageBus and returns what reached the broker.</summary>
        public async Task<OutgoingBrokerMessage> PublishAsync(IIntegrationEvent integrationEvent)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(integrationEvent, TestContext.Current.CancellationToken);
            return Services.GetRequiredService<InMemoryBroker>().Published[^1];
        }
    }

    private sealed class RecordingHandler(int failures, Action? onHandle) : IIntegrationEventHandler<PipelineEvent>
    {
        private int _calls;

        public ConcurrentQueue<PipelineEvent> ReceivedQueue { get; } = new();

        public IReadOnlyList<PipelineEvent> Received => [.. ReceivedQueue];

        public ConcurrentQueue<Activity?> ActivityQueue { get; } = new();

        public IReadOnlyList<Activity?> Activities => [.. ActivityQueue];

        public int Calls => _calls;

        public Task HandleAsync(PipelineEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            ActivityQueue.Enqueue(Activity.Current);
            onHandle?.Invoke();
            if (call <= failures)
            {
                throw new InvalidOperationException("handler down");
            }

            ReceivedQueue.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInbox : IInboxStore
    {
        public ConcurrentDictionary<Guid, bool> Completed { get; } = new();

        public Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult(Completed.ContainsKey(messageId));

        public Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        {
            Completed[messageId] = true;
            return Task.CompletedTask;
        }
    }
}
