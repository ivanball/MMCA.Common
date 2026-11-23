using Amazon.SQS.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Aws.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Aws.Tests.Messaging;

/// <summary>
/// How the transport applies each <see cref="BrokerDisposition"/> the receive pipeline returns, and the
/// visibility heartbeat, through a recording <see cref="IAmazonSqsSettlement"/>: no broker involved.
/// </summary>
public sealed class AmazonSqsDeliveryHandlerTests
{
    private static Message Received() => new()
    {
        MessageId = "sqs-1",
        ReceiptHandle = "receipt-1",
        Body = "{\"id\":1}",
        MessageAttributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
        {
            [AmazonSqsMessageMapping.HeadersAttribute] = new()
            {
                DataType = "String",
                StringValue = AmazonSqsMessageMapping.PackHeaders(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [BrokerHeaders.EventName] = "order-placed",
                    ["MMCA-Tenant-Id"] = "acme",
                }),
            },
        },
    };

    private static AmazonSqsDeliveryHandler HandlerReturning(
        Func<IncomingBrokerMessage, CancellationToken, Task<BrokerDisposition>> process,
        TimeProvider? timeProvider = null) =>
        new(new StubPipeline(process), timeProvider ?? new FakeTimeProvider(), NullLogger.Instance);

    [Fact]
    public async Task ThePipeline_SeesTheUnpackedHeadersAndTheBody()
    {
        IncomingBrokerMessage? seen = null;

        await HandlerReturning((incoming, _) =>
            {
                seen = incoming;
                return Task.FromResult(BrokerDisposition.Complete);
            })
            .HandleAsync(Received(), new RecordingSettlement(), TestContext.Current.CancellationToken);

        seen!.EventName.Should().Be("order-placed");
        seen.Headers["MMCA-Tenant-Id"].Should().Be("acme");
        System.Text.Encoding.UTF8.GetString(seen.Body.Span).Should().Be("{\"id\":1}");
        seen.BrokerMessageId.Should().Be("sqs-1");
    }

    [Fact]
    public async Task Complete_DeletesTheMessage()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult(BrokerDisposition.Complete))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("delete");
    }

    [Fact]
    public async Task Redeliver_SendsADelayedCopyToTheQueue_ThenDeletesTheOriginal()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.Redeliver(TimeSpan.FromMinutes(10), 2)))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("send-queue", "delete");
        settlement.SentDelaySeconds.Should().Be(600);
        settlement.SentBody.Should().Be("{\"id\":1}");
        settlement.SentHeaders![BrokerHeaders.EventName].Should().Be("order-placed");
        settlement.SentHeaders["MMCA-Tenant-Id"].Should().Be("acme");
        settlement.SentHeaders[BrokerHeaders.RedeliveryCount].Should().Be("2");
    }

    [Fact]
    public async Task Redeliver_BeyondTheSqsCap_IsClampedTo900Seconds()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.Redeliver(TimeSpan.FromHours(1), 1)))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.SentDelaySeconds.Should().Be(900);
    }

    [Fact]
    public async Task DeadLetter_SendsACopyToTheErrorQueueWithTheReasonAndDetail_ThenDeletesTheOriginal()
    {
        var settlement = new RecordingSettlement();

        await HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new BrokerDisposition.DeadLetter("handler-failed", "InvalidOperationException: down")))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("send-error", "delete");
        settlement.SentBody.Should().Be("{\"id\":1}");
        settlement.SentHeaders![AmazonSqsMessageMapping.DeadLetterReasonHeader].Should().Be("handler-failed");
        settlement.SentHeaders[AmazonSqsMessageMapping.DeadLetterDetailHeader].Should().Be("InvalidOperationException: down");
        settlement.SentHeaders[BrokerHeaders.EventName].Should().Be("order-placed");
    }

    [Fact]
    public async Task HostStopping_ReleasesTheMessage_AndNeverDeletesIt()
    {
        using var cts = new CancellationTokenSource();
        var settlement = new RecordingSettlement();

        await HandlerReturning(async (_, token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return BrokerDisposition.Complete;
            })
            .HandleAsync(Received(), settlement, cts.Token);

        settlement.Calls.Should().Equal("visibility:0");
    }

    [Fact]
    public async Task HostStopping_WhenTheReleaseFails_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var settlement = new RecordingSettlement { FailVisibility = true };

        var act = () => HandlerReturning(async (_, token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return BrokerDisposition.Complete;
            })
            .HandleAsync(Received(), settlement, cts.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AHandlersOwnCancellation_WithTheHostRunning_Propagates()
    {
        var settlement = new RecordingSettlement();

        var act = () => HandlerReturning((_, _) => throw new OperationCanceledException("handler's own"))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        settlement.Calls.Should().BeEmpty("the message reappears when its visibility timeout runs out");
    }

    [Fact]
    public async Task WhileThePipelineRuns_TheHeartbeatExtendsTheVisibility()
    {
        var time = new FakeTimeProvider();
        var settlement = new RecordingSettlement();
        var release = new TaskCompletionSource<BrokerDisposition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handling = HandlerReturning(
                (_, _) =>
                {
                    started.TrySetResult();
                    return release.Task;
                },
                time)
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await settlement.AdvanceUntilCallsAsync(time, 2);

        release.SetResult(BrokerDisposition.Complete);
        await handling;

        var calls = settlement.Calls;
        calls[^1].Should().Be("delete");
        calls.Take(calls.Count - 1).Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(c => c == "visibility:60");
    }

    [Fact]
    public async Task NoHeartbeat_BeforeTheFirstInterval()
    {
        var time = new FakeTimeProvider();
        var settlement = new RecordingSettlement();

        await HandlerReturning(
                (_, _) =>
                {
                    time.Advance(AmazonSqsDeliveryHandler.HeartbeatInterval - TimeSpan.FromSeconds(1));
                    return Task.FromResult(BrokerDisposition.Complete);
                },
                time)
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        settlement.Calls.Should().Equal("delete");
    }

    [Fact]
    public async Task AFailedHeartbeat_DoesNotFailTheMessage()
    {
        var time = new FakeTimeProvider();
        var settlement = new RecordingSettlement { FailVisibility = true };
        var release = new TaskCompletionSource<BrokerDisposition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handling = HandlerReturning(
                (_, _) =>
                {
                    started.TrySetResult();
                    return release.Task;
                },
                time)
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await settlement.AdvanceUntilCallsAsync(time, 2);

        release.SetResult(BrokerDisposition.Complete);
        await handling;

        settlement.Calls[^1].Should().Be("delete");
    }

    [Fact]
    public async Task AnUnknownDisposition_Throws_AndSettlesNothing()
    {
        var settlement = new RecordingSettlement();

        var act = () => HandlerReturning((_, _) => Task.FromResult<BrokerDisposition>(new UnknownDisposition()))
            .HandleAsync(Received(), settlement, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UnknownDisposition*");
        settlement.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        var pipeline = new StubPipeline((_, _) => Task.FromResult(BrokerDisposition.Complete));

        var noPipeline = () => new AmazonSqsDeliveryHandler(null!, TimeProvider.System, NullLogger.Instance);
        var noTime = () => new AmazonSqsDeliveryHandler(pipeline, null!, NullLogger.Instance);
        var noLogger = () => new AmazonSqsDeliveryHandler(pipeline, TimeProvider.System, null!);

        noPipeline.Should().Throw<ArgumentNullException>();
        noTime.Should().Throw<ArgumentNullException>();
        noLogger.Should().Throw<ArgumentNullException>();
    }

    private sealed record UnknownDisposition : BrokerDisposition;

    private sealed class StubPipeline(Func<IncomingBrokerMessage, CancellationToken, Task<BrokerDisposition>> process) : IBrokerReceivePipeline
    {
        public Task<BrokerDisposition> ProcessAsync(IncomingBrokerMessage message, CancellationToken cancellationToken) =>
            process(message, cancellationToken);
    }

    private sealed class RecordingSettlement : IAmazonSqsSettlement
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];

        public bool FailVisibility { get; init; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public string? SentBody { get; private set; }

        public IReadOnlyDictionary<string, string>? SentHeaders { get; private set; }

        public int? SentDelaySeconds { get; private set; }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            Record("delete");
            return Task.CompletedTask;
        }

        public Task SendToQueueAsync(string body, IReadOnlyDictionary<string, string> headers, int delaySeconds, CancellationToken cancellationToken)
        {
            (SentBody, SentHeaders, SentDelaySeconds) = (body, headers, delaySeconds);
            Record("send-queue");
            return Task.CompletedTask;
        }

        public Task SendToErrorQueueAsync(string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            (SentBody, SentHeaders) = (body, headers);
            Record("send-error");
            return Task.CompletedTask;
        }

        public Task ChangeVisibilityAsync(int visibilityTimeoutSeconds, CancellationToken cancellationToken)
        {
            Record("visibility:" + visibilityTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return FailVisibility ? Task.FromException(new InvalidOperationException("visibility down")) : Task.CompletedTask;
        }

        /// <summary>
        /// Advances the fake clock one heartbeat interval at a time until at least <paramref name="count"/>
        /// calls were recorded (each beat schedules the next delay only after its call, so one advance per
        /// observed beat would race it).
        /// </summary>
        public async Task AdvanceUntilCallsAsync(FakeTimeProvider time, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Calls.Count < count && DateTime.UtcNow < deadline)
            {
                time.Advance(AmazonSqsDeliveryHandler.HeartbeatInterval);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Calls.Should().HaveCountGreaterThanOrEqualTo(count);
        }

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }
    }
}
