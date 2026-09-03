using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="EventUpcasterRegistry"/> (ADR-090): identity for an unregistered
/// contract, one hop, a whole V1 to V2 to V3 ladder applied in order, envelope preservation across
/// every hop (whether or not the author copies it), and the three constructor-time misconfigurations
/// that must fail loudly (duplicate source, cycle, self-map).
/// <para>
/// The sample contracts below live in this TEST assembly only, so the frozen integration-event
/// contract snapshot of the framework never churns on them.
/// </para>
/// </summary>
public sealed class EventUpcasterRegistryTests
{
    // ── Sample contract ladder: V1 carries one composite field, V2 adds a trace of the hops taken,
    //    V3 splits the composite field. Every hop appends to Trace, so ORDER is observable. ──
    private sealed record CustomerRenamedV1(string FullName) : BaseIntegrationEvent;

    private sealed record CustomerRenamedV2(string FullName, string Trace) : BaseIntegrationEvent
    {
        public override int SchemaVersion => 2;
    }

    private sealed record CustomerRenamedV3(string FirstName, string LastName, string Trace) : BaseIntegrationEvent
    {
        public override int SchemaVersion => 3;
    }

    private sealed record UnrelatedEvent(string Data) : BaseIntegrationEvent;

    // ── Upcasters: payload-only mapping, exactly as the doc comments instruct authors to write them ──
    private sealed class V1ToV2Upcaster : IEventUpcaster<CustomerRenamedV1, CustomerRenamedV2>
    {
        public CustomerRenamedV2 Upcast(CustomerRenamedV1 integrationEvent) =>
            new(integrationEvent.FullName, "v1->v2");
    }

    private sealed class V2ToV3Upcaster : IEventUpcaster<CustomerRenamedV2, CustomerRenamedV3>
    {
        public CustomerRenamedV3 Upcast(CustomerRenamedV2 integrationEvent)
        {
            var parts = integrationEvent.FullName.Split(' ');
            return new CustomerRenamedV3(parts[0], parts[^1], integrationEvent.Trace + "|v2->v3");
        }
    }

    /// <summary>An author who copies the envelope by hand: the registry stamp must be idempotent.</summary>
    private sealed class EnvelopeCopyingV1ToV2Upcaster : IEventUpcaster<CustomerRenamedV1, CustomerRenamedV2>
    {
        public CustomerRenamedV2 Upcast(CustomerRenamedV1 integrationEvent) =>
            new(integrationEvent.FullName, "v1->v2")
            {
                MessageId = integrationEvent.MessageId,
                DateOccurred = integrationEvent.DateOccurred,
            };
    }

    /// <summary>A second claimant on <c>CustomerRenamedV1</c>, used for the duplicate-source case.</summary>
    private sealed class RivalV1ToV3Upcaster : IEventUpcaster<CustomerRenamedV1, CustomerRenamedV3>
    {
        public CustomerRenamedV3 Upcast(CustomerRenamedV1 integrationEvent) =>
            new("rival", integrationEvent.FullName, "rival");
    }

    /// <summary>Closes a cycle back onto V1, used for the cycle case.</summary>
    private sealed class V2ToV1Upcaster : IEventUpcaster<CustomerRenamedV2, CustomerRenamedV1>
    {
        public CustomerRenamedV1 Upcast(CustomerRenamedV2 integrationEvent) =>
            new(integrationEvent.FullName);
    }

    /// <summary>Maps a contract onto itself, used for the self-map case.</summary>
    private sealed class SelfMappingUpcaster : IEventUpcaster<CustomerRenamedV1, CustomerRenamedV1>
    {
        public CustomerRenamedV1 Upcast(CustomerRenamedV1 integrationEvent) => integrationEvent;
    }

    private static EventUpcasterRegistry CreateSut(params IEventUpcaster[] upcasters) => new(upcasters);

    // ── Identity: a contract nobody claims comes back untouched ──
    [Fact]
    public void UpcastToTerminal_WithNoUpcasterForTheType_ReturnsTheSameInstance()
    {
        var sut = CreateSut(new V1ToV2Upcaster());
        var unrelated = new UnrelatedEvent("payload");

        var result = sut.UpcastToTerminal(unrelated);

        result.Should().BeSameAs(unrelated);
        sut.HasUpcasterFor(typeof(UnrelatedEvent)).Should().BeFalse();
        sut.ResolveTerminalType(typeof(UnrelatedEvent)).Should().Be<UnrelatedEvent>();
    }

    // ── Single hop: the payload mapping the author wrote is what lands ──
    [Fact]
    public void UpcastToTerminal_WithSingleHop_AppliesThePayloadMapping()
    {
        var sut = CreateSut(new V1ToV2Upcaster());

        var result = sut.UpcastToTerminal(new CustomerRenamedV1("Ada Lovelace"));

        result.Should().BeOfType<CustomerRenamedV2>()
            .Which.FullName.Should().Be("Ada Lovelace");
        sut.HasUpcasterFor(typeof(CustomerRenamedV1)).Should().BeTrue();
        sut.ResolveTerminalType(typeof(CustomerRenamedV1)).Should().Be<CustomerRenamedV2>();
    }

    // ── Chain: both hops run, and they run in ladder order ──
    [Fact]
    public void UpcastToTerminal_WithChain_AppliesEveryHopInOrder()
    {
        var sut = CreateSut(new V1ToV2Upcaster(), new V2ToV3Upcaster());

        var result = sut.UpcastToTerminal(new CustomerRenamedV1("Ada Lovelace"));

        var terminal = result.Should().BeOfType<CustomerRenamedV3>().Which;
        terminal.FirstName.Should().Be("Ada");
        terminal.LastName.Should().Be("Lovelace");
        terminal.Trace.Should().Be("v1->v2|v2->v3", "each hop appends to the trace as it runs");
    }

    // ── Chain: registration order at the container must not change the walk ──
    [Fact]
    public void UpcastToTerminal_WithChainRegisteredOutOfOrder_StillWalksTheLadder()
    {
        var sut = CreateSut(new V2ToV3Upcaster(), new V1ToV2Upcaster());

        var result = sut.UpcastToTerminal(new CustomerRenamedV1("Ada Lovelace"));

        result.Should().BeOfType<CustomerRenamedV3>()
            .Which.Trace.Should().Be("v1->v2|v2->v3");
    }

    // ── Terminal type resolution follows the whole chain, not just the first hop ──
    [Fact]
    public void ResolveTerminalType_WithChain_ReturnsTheEndOfTheLadder()
    {
        var sut = CreateSut(new V1ToV2Upcaster(), new V2ToV3Upcaster());

        sut.ResolveTerminalType(typeof(CustomerRenamedV1)).Should().Be<CustomerRenamedV3>();
        sut.ResolveTerminalType(typeof(CustomerRenamedV2)).Should().Be<CustomerRenamedV3>();
        sut.ResolveTerminalType(typeof(CustomerRenamedV3)).Should().Be<CustomerRenamedV3>();
    }

    // ── Envelope: preserved across EVERY hop even though neither upcaster copies it ──
    [Fact]
    public void UpcastToTerminal_AcrossMultipleHops_PreservesMessageIdAndDateOccurred()
    {
        var sut = CreateSut(new V1ToV2Upcaster(), new V2ToV3Upcaster());
        var original = new CustomerRenamedV1("Ada Lovelace")
        {
            MessageId = Guid.NewGuid(),
            DateOccurred = new DateTime(2024, 3, 14, 9, 26, 53, DateTimeKind.Utc),
        };

        var result = sut.UpcastToTerminal(original);

        result.MessageId.Should().Be(original.MessageId, "inbox deduplication stays keyed on the id the producer published");
        result.DateOccurred.Should().Be(original.DateOccurred);
    }

    // ── Envelope: an author who copies it by hand gets the same values written twice, not a conflict ──
    [Fact]
    public void UpcastToTerminal_WhenTheUpcasterCopiesTheEnvelope_IsIdempotent()
    {
        var sut = CreateSut(new EnvelopeCopyingV1ToV2Upcaster());
        var original = new CustomerRenamedV1("Ada Lovelace")
        {
            MessageId = Guid.NewGuid(),
            DateOccurred = new DateTime(2024, 3, 14, 9, 26, 53, DateTimeKind.Utc),
        };

        var result = sut.UpcastToTerminal(original);

        result.MessageId.Should().Be(original.MessageId);
        result.DateOccurred.Should().Be(original.DateOccurred);
    }

    // ── Empty registry: every operation is identity ──
    [Fact]
    public void UpcastToTerminal_WithNoRegistrationsAtAll_IsIdentity()
    {
        var sut = CreateSut();
        var original = new CustomerRenamedV1("Ada Lovelace");

        sut.UpcastToTerminal(original).Should().BeSameAs(original);
        sut.HasUpcasterFor(typeof(CustomerRenamedV1)).Should().BeFalse();
        sut.ResolveTerminalType(typeof(CustomerRenamedV1)).Should().Be<CustomerRenamedV1>();
    }

    // ── Misconfiguration: two upcasters claiming one source, named in the message ──
    [Fact]
    public void Constructor_WithTwoUpcastersClaimingOneSource_ThrowsNamingBoth()
    {
        var act = () => CreateSut(new V1ToV2Upcaster(), new RivalV1ToV3Upcaster());

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(nameof(CustomerRenamedV1));
        message.Should().Contain(nameof(V1ToV2Upcaster));
        message.Should().Contain(nameof(RivalV1ToV3Upcaster));
    }

    // ── Misconfiguration: a chain that loops back would never terminate ──
    [Fact]
    public void Constructor_WithCyclicChain_ThrowsNamingTheCycle()
    {
        var act = () => CreateSut(new V1ToV2Upcaster(), new V2ToV1Upcaster());

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("cycle");
        message.Should().Contain(nameof(CustomerRenamedV1));
        message.Should().Contain(nameof(CustomerRenamedV2));
    }

    // ── Misconfiguration: a source mapped onto itself is a cycle of length one ──
    [Fact]
    public void Constructor_WithSourceEqualToTarget_ThrowsNamingTheUpcaster()
    {
        var act = () => CreateSut(new SelfMappingUpcaster());

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(nameof(SelfMappingUpcaster));
        message.Should().Contain("onto itself");
    }

    // ── Null guards ──
    [Fact]
    public void Constructor_WithNullUpcasters_ThrowsArgumentNullException()
    {
        var act = () => new EventUpcasterRegistry(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("upcasters");
    }

    [Fact]
    public void UpcastToTerminal_WithNullEvent_ThrowsArgumentNullException()
    {
        var sut = CreateSut();

        var act = () => sut.UpcastToTerminal(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("integrationEvent");
    }

    [Fact]
    public void TypeProbes_WithNullType_ThrowArgumentNullException()
    {
        var sut = CreateSut();

        FluentActions.Invoking(() => sut.HasUpcasterFor(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => sut.ResolveTerminalType(null!)).Should().Throw<ArgumentNullException>();
    }

    // ── The registry is an IIntegrationEvent pipeline: the walk advances by DECLARED target type ──
    [Fact]
    public void UpcastToTerminal_ReturnsAnIntegrationEvent_ForEveryHop()
    {
        var sut = CreateSut(new V1ToV2Upcaster(), new V2ToV3Upcaster());

        IIntegrationEvent result = sut.UpcastToTerminal(new CustomerRenamedV1("Ada Lovelace"));

        result.Should().BeAssignableTo<IIntegrationEvent>();
        result.GetType().Should().Be(sut.ResolveTerminalType(typeof(CustomerRenamedV1)));
    }
}
