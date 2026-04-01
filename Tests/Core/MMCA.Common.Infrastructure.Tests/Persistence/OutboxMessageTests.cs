using AwesomeAssertions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Outbox;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="OutboxMessage"/> covering creation from domain events,
/// serialization, deserialization, and edge cases.
/// </summary>
public sealed class OutboxMessageTests
{
    private sealed record TestDomainEvent : BaseDomainEvent;

    private sealed record TestDomainEventWithData(string Name, int Value) : BaseDomainEvent;

    // ── FromDomainEvent ──
    [Fact]
    public void FromDomainEvent_SetsEventType()
    {
        var domainEvent = new TestDomainEvent();

        var message = OutboxMessage.FromDomainEvent(domainEvent);

        message.EventType.Should().Contain(nameof(TestDomainEvent));
    }

    [Fact]
    public void FromDomainEvent_SetsPayloadAsJson()
    {
        var domainEvent = new TestDomainEventWithData("Test", 42);

        var message = OutboxMessage.FromDomainEvent(domainEvent);

        message.Payload.Should().Contain("\"Name\":\"Test\"");
        message.Payload.Should().Contain("\"Value\":42");
    }

    [Fact]
    public void FromDomainEvent_SetsOccurredOn()
    {
        var domainEvent = new TestDomainEvent();

        var message = OutboxMessage.FromDomainEvent(domainEvent);

        message.OccurredOn.Should().Be(domainEvent.DateOccurred);
    }

    [Fact]
    public void FromDomainEvent_GeneratesUniqueIds()
    {
        var event1 = new TestDomainEvent();
        var event2 = new TestDomainEvent();

        var message1 = OutboxMessage.FromDomainEvent(event1);
        var message2 = OutboxMessage.FromDomainEvent(event2);

        message1.Id.Should().NotBe(message2.Id);
    }

    [Fact]
    public void FromDomainEvent_ProcessedOnIsNull()
    {
        var domainEvent = new TestDomainEvent();

        var message = OutboxMessage.FromDomainEvent(domainEvent);

        message.ProcessedOn.Should().BeNull();
    }

    [Fact]
    public void FromDomainEvent_RetryCountIsZero()
    {
        var domainEvent = new TestDomainEvent();

        var message = OutboxMessage.FromDomainEvent(domainEvent);

        message.RetryCount.Should().Be(0);
    }

    [Fact]
    public void FromDomainEvent_NullArgument_Throws()
    {
        Action act = () => OutboxMessage.FromDomainEvent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── DeserializeEvent ──
    [Fact]
    public void DeserializeEvent_RoundTrips_SimpleEvent()
    {
        var domainEvent = new TestDomainEvent();
        var message = OutboxMessage.FromDomainEvent(domainEvent);

        var deserialized = message.DeserializeEvent();

        deserialized.Should().NotBeNull();
        deserialized.Should().BeOfType<TestDomainEvent>();
    }

    [Fact]
    public void DeserializeEvent_RoundTrips_EventWithData()
    {
        var domainEvent = new TestDomainEventWithData("Hello", 99);
        var message = OutboxMessage.FromDomainEvent(domainEvent);

        var deserialized = message.DeserializeEvent();

        deserialized.Should().NotBeNull();
        var typed = deserialized.Should().BeOfType<TestDomainEventWithData>().Subject;
        typed.Name.Should().Be("Hello");
        typed.Value.Should().Be(99);
    }

    [Fact]
    public void DeserializeEvent_UnresolvableType_ReturnsNull()
    {
        var message = new OutboxMessage
        {
            EventType = "NonExistent.Namespace.FakeEvent, FakeAssembly",
            Payload = "{}",
            OccurredOn = DateTime.UtcNow
        };

        var result = message.DeserializeEvent();

        result.Should().BeNull();
    }

    // ── Mutable properties ──
    [Fact]
    public void ProcessedOn_CanBeSet()
    {
        var message = OutboxMessage.FromDomainEvent(new TestDomainEvent());
        var processedTime = DateTime.UtcNow;

        message.ProcessedOn = processedTime;

        message.ProcessedOn.Should().Be(processedTime);
    }

    [Fact]
    public void RetryCount_CanBeIncremented()
    {
        var message = OutboxMessage.FromDomainEvent(new TestDomainEvent());

        message.RetryCount = 3;

        message.RetryCount.Should().Be(3);
    }

    [Fact]
    public void LastError_CanBeSet()
    {
        var message = OutboxMessage.FromDomainEvent(new TestDomainEvent());

        message.LastError = "Connection timeout";

        message.LastError.Should().Be("Connection timeout");
    }
}
