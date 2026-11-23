using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// How the transport applies each <see cref="BrokerDisposition"/> the receive pipeline returns, through
/// a recording <see cref="IAzureServiceBusSettlement"/>: no broker involved.
/// </summary>
public sealed class AzureServiceBusDeliveryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static ServiceBusReceivedMessage Received() =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: "m-1",
            contentType: "application/json",
            properties: new Dictionary<string, object>
            {
                [BrokerHeaders.EventName] = "order-placed",
                ["MMCA-TenantId"] = "acme",
            });

    private static AzureServiceBusDeliveryHandler HandlerReturning(Func<IncomingBrokerMessage, CancellationToken, Task<BrokerDisposition>> process) =>
        new(new StubPipeline(process), new FixedTimeProvider(Now), NullLogger.Instance);

    [Fact]
    public async Task Complete_CompletesTheMessage()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult(BrokerDisposition.Complete))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("complete");
    }

    [Fact]
    public async Task Redeliver_SchedulesACopyOnTheQueue_ThenCompletesTheOriginal()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.Redeliver(TimeSpan.FromMinutes(10), 2)))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("schedule", "complete");
        settlement.ScheduledAt.Should().Be(Now + TimeSpan.FromMinutes(10));
        var copy = settlement.Scheduled!;
        copy.MessageId.Should().Be("m-1");
        copy.ContentType.Should().Be("application/json");
        copy.Body.ToString().Should().Be("{}");
        copy.ApplicationProperties[BrokerHeaders.EventName].Should().Be("order-placed");
        copy.ApplicationProperties["MMCA-TenantId"].Should().Be("acme");
        copy.ApplicationProperties[BrokerHeaders.RedeliveryCount].Should().Be("2");
    }

    [Fact]
    public async Task DeadLetter_MovesTheMessageWithTheReasonAndTheDetail()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.DeadLetter("handler-failed", "InvalidOperationException: down")))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("dead-letter");
        settlement.DeadLetterReason.Should().Be("handler-failed");
        settlement.DeadLetterDescription.Should().Be("InvalidOperationException: down");
    }

    [Fact]
    public async Task DeadLetter_TruncatesTheDetailToTheSdkLimit()
    {
        var settlement = new RecordingSettlement();
        var detail = new string('x', AzureServiceBusDeliveryHandler.MaxDeadLetterTextLength + 100);

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.DeadLetter("poison", detail)))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.DeadLetterDescription.Should().HaveLength(AzureServiceBusDeliveryHandler.MaxDeadLetterTextLength);
    }

    [Fact]
    public async Task DeadLetter_WithNoDetail_PassesNull()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.DeadLetter("unknown-event", null)))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.DeadLetterDescription.Should().BeNull();
    }

    [Fact]
    public async Task TheHostStopping_AbandonsTheMessage()
    {
        var settlement = new RecordingSettlement();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await HandlerReturning((_, token) => Task.FromCanceled<BrokerDisposition>(token))
            .HandleAsync(Received(), settlement, stopping.Token);

        settlement.Calls.Should().Equal("abandon");
    }

    [Fact]
    public async Task AFailedAbandon_DuringShutdown_IsSwallowed()
    {
        var settlement = new RecordingSettlement { FailAbandon = true };
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var act = () => HandlerReturning((_, token) => Task.FromCanceled<BrokerDisposition>(token))
            .HandleAsync(Received(), settlement, stopping.Token);

        await act.Should().NotThrowAsync("an unabandoned message is redelivered when its lock expires anyway");
    }

    [Fact]
    public async Task ACancellationThatIsNotTheHosts_Propagates_SoTheProcessorAbandonsAndReports()
    {
        var settlement = new RecordingSettlement();

        var act = () => HandlerReturning((_, _) => throw new OperationCanceledException())
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        settlement.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePipeline_SeesTheMessageHeadersAndId()
    {
        IncomingBrokerMessage? seen = null;

        await HandlerReturning((message, _) =>
            {
                seen = message;
                return Task.FromResult(BrokerDisposition.Complete);
            })
            .HandleAsync(Received(), new RecordingSettlement(), TestContext.Current.CancellationToken);

        seen!.EventName.Should().Be("order-placed");
        seen.Headers["MMCA-TenantId"].Should().Be("acme");
        seen.BrokerMessageId.Should().Be("m-1");
    }

    private sealed class StubPipeline(Func<IncomingBrokerMessage, CancellationToken, Task<BrokerDisposition>> process) : IBrokerReceivePipeline
    {
        public Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken) =>
            process(message, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSettlement : IAzureServiceBusSettlement
    {
        public List<string> Calls { get; } = [];

        public bool FailAbandon { get; init; }

        public ServiceBusMessage? Scheduled { get; private set; }

        public DateTimeOffset? ScheduledAt { get; private set; }

        public string? DeadLetterReason { get; private set; }

        public string? DeadLetterDescription { get; private set; }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Calls.Add("complete");
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string reason, string? description, CancellationToken cancellationToken)
        {
            Calls.Add("dead-letter");
            DeadLetterReason = reason;
            DeadLetterDescription = description;
            return Task.CompletedTask;
        }

        public Task AbandonAsync(CancellationToken cancellationToken)
        {
            Calls.Add("abandon");
            return FailAbandon ? Task.FromException(new ServiceBusException("connection closed", ServiceBusFailureReason.ServiceCommunicationProblem)) : Task.CompletedTask;
        }

        public Task ScheduleOnQueueAsync(ServiceBusMessage message, DateTimeOffset enqueueTime, CancellationToken cancellationToken)
        {
            Calls.Add("schedule");
            Scheduled = message;
            ScheduledAt = enqueueTime;
            return Task.CompletedTask;
        }
    }
}
