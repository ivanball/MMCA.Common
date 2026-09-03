using System.Diagnostics.Metrics;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Consumers;

/// <summary>
/// Unit tests for <see cref="FaultIntegrationEventConsumer{TEvent}"/>: the observability path a
/// faulted integration event takes once MassTransit has given up retrying it.
/// </summary>
public sealed class FaultIntegrationEventConsumerTests
{
    public sealed record class TestFaultedEvent : BaseIntegrationEvent;

    private static Mock<ConsumeContext<Fault<TestFaultedEvent>>> ContextFor(Fault<TestFaultedEvent> fault)
    {
        var context = new Mock<ConsumeContext<Fault<TestFaultedEvent>>>();
        context.SetupGet(c => c.Message).Returns(fault);
        return context;
    }

    private static Fault<TestFaultedEvent> FaultWith(Guid? faultedMessageId, params string[] exceptionMessages)
    {
        var fault = new Mock<Fault<TestFaultedEvent>>();
        fault.SetupGet(f => f.FaultId).Returns(Guid.NewGuid());
        fault.SetupGet(f => f.FaultedMessageId).Returns(faultedMessageId);
        fault.SetupGet(f => f.Exceptions).Returns(
            [.. exceptionMessages.Select(m => Mock.Of<ExceptionInfo>(e => e.Message == m))]);
        return fault.Object;
    }

    /// <summary>
    /// Captures the rendered text and level of every log call so the test asserts on what an
    /// operator actually reads, not on an EventId. Same shape as the OutboxProcessor log tests.
    /// </summary>
    private static Mock<ILogger<FaultIntegrationEventConsumer<TestFaultedEvent>>> CapturingLogger(
        List<(LogLevel Level, string Message)> sink)
    {
        var logger = new Mock<ILogger<FaultIntegrationEventConsumer<TestFaultedEvent>>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var formatter = (Delegate)invocation.Arguments[4];
                sink.Add((
                    (LogLevel)invocation.Arguments[0],
                    (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!));
            }));
        return logger;
    }

    [Fact]
    public async Task Consume_LogsOneErrorNamingTheEventAndEveryExceptionMessage()
    {
        var faultedMessageId = Guid.NewGuid();
        var logged = new List<(LogLevel Level, string Message)>();
        var sut = new FaultIntegrationEventConsumer<TestFaultedEvent>(CapturingLogger(logged).Object);

        await sut.Consume(ContextFor(FaultWith(faultedMessageId, "outer boom", "inner boom")).Object);

        logged.Should().ContainSingle();
        (LogLevel Level, string Message) entry = logged[0];
        entry.Level.Should().Be(LogLevel.Error, "a lost integration event is an operator-actionable failure");
        entry.Message.Should().Contain(nameof(TestFaultedEvent));
        entry.Message.Should().Contain(faultedMessageId.ToString());
        entry.Message.Should().Contain("outer boom");
        entry.Message.Should().Contain("inner boom", "the whole exception chain identifies the cause");
    }

    [Fact]
    public async Task Consume_IncrementsFaultCounter_TaggedByEventType()
    {
        var gate = new System.Threading.Lock();
        var measurements = new List<(long Value, string? EventType)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "MMCA.Common.Broker", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "broker.fault.count", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? eventType = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, "event_type", StringComparison.Ordinal))
                {
                    eventType = tag.Value as string;
                }
            }

            lock (gate)
            {
                measurements.Add((value, eventType));
            }
        });
        listener.Start();

        var sut = new FaultIntegrationEventConsumer<TestFaultedEvent>(
            Mock.Of<ILogger<FaultIntegrationEventConsumer<TestFaultedEvent>>>());

        await sut.Consume(ContextFor(FaultWith(Guid.NewGuid(), "boom")).Object);

        measurements.Should().Contain((1L, nameof(TestFaultedEvent)));
    }

    [Fact]
    public async Task Consume_FallsBackToFaultId_WhenNoFaultedMessageIdWasCaptured()
    {
        var logged = new List<(LogLevel Level, string Message)>();
        var sut = new FaultIntegrationEventConsumer<TestFaultedEvent>(CapturingLogger(logged).Object);

        await sut.Consume(ContextFor(FaultWith(faultedMessageId: null, "boom")).Object);

        logged.Should().ContainSingle();
        logged[0].Message.Should().NotContain(Guid.Empty.ToString(), "the fault id stands in when no message id was captured");
    }

    [Fact]
    public async Task Consume_DoesNotThrow_WhenTheFaultCarriesNoExceptionDetail()
    {
        // A fault consumer that faults would publish Fault<Fault<TEvent>> and, with second-level
        // redelivery on, keep re-entering itself. Observability code must not create incidents.
        var sut = new FaultIntegrationEventConsumer<TestFaultedEvent>(
            Mock.Of<ILogger<FaultIntegrationEventConsumer<TestFaultedEvent>>>());

        var act = async () => await sut.Consume(ContextFor(FaultWith(Guid.NewGuid())).Object);

        await act.Should().NotThrowAsync();
    }
}
