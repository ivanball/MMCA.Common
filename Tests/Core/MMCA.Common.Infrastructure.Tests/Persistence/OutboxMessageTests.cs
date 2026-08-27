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

    private sealed record OrderedDomainEvent(string? Key) : BaseDomainEvent, IHasOrderingKey
    {
        public string? OrderingKey => Key;
    }

    // ── Ordering key ──
    [Fact]
    public void FromDomainEvent_CopiesTheOrderingKey_WhenTheEventDeclaresOne()
    {
        var message = OutboxMessage.FromDomainEvent(new OrderedDomainEvent("cart-42"));

        message.OrderingKey.Should().Be("cart-42");
    }

    [Fact]
    public void FromDomainEvent_LeavesTheOrderingKeyNull_WhenTheEventDoesNotOptIn()
    {
        OutboxMessage.FromDomainEvent(new TestDomainEvent()).OrderingKey.Should().BeNull();

        // An implementing event may still opt an individual instance out by returning null, which is
        // why the row copies the VALUE rather than a type-level flag.
        OutboxMessage.FromDomainEvent(new OrderedDomainEvent(null)).OrderingKey.Should().BeNull();
    }

    // ── Type aliases: a renamed or relocated contract still deserializes ──
    [Fact]
    public void DeserializeEvent_WithoutAnAlias_ReturnsNullForARetiredTypeName()
    {
        var message = new OutboxMessage
        {
            EventType = "Gone.Namespace.GoneEvent, GoneAssembly",
            Payload = """{"Name":"Test","Value":42}""",
        };

        message.DeserializeEvent().Should().BeNull();
    }

    [Fact]
    public void DeserializeEvent_WithAnAlias_ResolvesTheReplacementTypeAndReadsThePayload()
    {
        var message = new OutboxMessage
        {
            EventType = "Gone.Namespace.GoneEvent, GoneAssembly",
            Payload = """{"Name":"Test","Value":42}""",
        };

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gone.Namespace.GoneEvent, GoneAssembly"] = typeof(TestDomainEventWithData).AssemblyQualifiedName!,
        };

        message.DeserializeEvent(aliases).Should().BeOfType<TestDomainEventWithData>()
            .Which.Should().BeEquivalentTo(new { Name = "Test", Value = 42 });
    }

    [Fact]
    public void DeserializeEvent_AliasKeyedByTypeFullName_AlsoResolves()
    {
        // Operators write type names in configuration, not assembly-qualified names.
        var message = new OutboxMessage
        {
            EventType = "Gone.Namespace.OtherGoneEvent, GoneAssembly",
            Payload = """{"Name":"Test","Value":1}""",
        };

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gone.Namespace.OtherGoneEvent"] = typeof(TestDomainEventWithData).AssemblyQualifiedName!,
        };

        message.DeserializeEvent(aliases).Should().BeOfType<TestDomainEventWithData>();
    }

    [Fact]
    public void DeserializeEvent_AliasTargetWithoutAnAssembly_IsFoundAmongTheLoadedAssemblies()
    {
        var message = new OutboxMessage
        {
            EventType = "Gone.Namespace.BareTargetEvent, GoneAssembly",
            Payload = """{"Name":"Test","Value":7}""",
        };

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gone.Namespace.BareTargetEvent"] = typeof(TestDomainEventWithData).FullName!,
        };

        message.DeserializeEvent(aliases).Should().BeOfType<TestDomainEventWithData>();
    }

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
