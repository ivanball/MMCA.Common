#pragma warning disable CA2263 // The SUT deliberately calls the non-generic Publish(object, Type, CancellationToken) overload; mock setups and verifies must match that exact overload.

using AwesomeAssertions;
using MassTransit;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Messaging;
using Moq;

// Aliased for the same reason the bus itself aliases it: MassTransit ships its own static
// MessageHeaders, so the bare name is ambiguous here.
using MessageHeaders = MMCA.Common.Shared.Messaging.MessageHeaders;

namespace MMCA.Common.Infrastructure.Tests.Messaging;

/// <summary>
/// Tests for <see cref="BrokerMessageBus"/>: a thin adapter over MassTransit's
/// <see cref="IPublishEndpoint"/>. The contract under guard is publish-by-runtime-type
/// (so the broker routes by the concrete event class, not the interface), payload
/// passthrough by reference, token propagation, and unwrapped exception propagation.
/// Broker topology behavior itself is integration-tier and deliberately not tested here.
/// </summary>
public sealed class BrokerMessageBusTests
{
    public sealed record class TestIntegrationEvent : BaseIntegrationEvent;

    public sealed record class OtherIntegrationEvent : BaseIntegrationEvent;

    // ── Mocks ──
    private sealed record Mocks(Mock<IPublishEndpoint> PublishEndpoint);

    // ── Factory ──
    private static (BrokerMessageBus Sut, Mocks Mocks) CreateSut()
    {
        var publishEndpoint = new Mock<IPublishEndpoint>();
        publishEndpoint
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<Type>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (new BrokerMessageBus(publishEndpoint.Object), new Mocks(publishEndpoint));
    }

    // ── Null guard: single event ──
    [Fact]
    public async Task PublishAsync_NullEvent_ThrowsArgumentNullException()
    {
        var (sut, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IIntegrationEvent)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvent");
    }

    // ── Null guard: batch overload ──
    [Fact]
    public async Task PublishAsync_NullEventCollection_ThrowsArgumentNullException()
    {
        var (sut, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IEnumerable<IIntegrationEvent>)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvents");
    }

    // ── Single event: same instance published under its concrete runtime type ──
    [Fact]
    public async Task PublishAsync_SingleEvent_PublishesSameInstanceWithRuntimeType()
    {
        var (sut, mocks) = CreateSut();
        var integrationEvent = new TestIntegrationEvent();

        await sut.PublishAsync(integrationEvent, CancellationToken.None);

        mocks.PublishEndpoint.Verify(
            p => p.Publish(
                It.Is<object>(m => ReferenceEquals(m, integrationEvent)),
                typeof(TestIntegrationEvent),
                CancellationToken.None),
            Times.Once);
        mocks.PublishEndpoint.VerifyNoOtherCalls();
    }

    // ── Single event: exact token forwarded to the endpoint ──
    [Fact]
    public async Task PublishAsync_SingleEvent_PropagatesCancellationToken()
    {
        var (sut, mocks) = CreateSut();
        var integrationEvent = new TestIntegrationEvent();
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync(integrationEvent, cts.Token);

        mocks.PublishEndpoint.Verify(
            p => p.Publish(It.IsAny<object>(), It.IsAny<Type>(), cts.Token),
            Times.Once);
    }

    // ── Single event: a broker failure propagates unwrapped (no swallowing, no mapping) ──
    [Fact]
    public async Task PublishAsync_SingleEvent_WhenBrokerPublishFaults_PropagatesSameException()
    {
        var (sut, mocks) = CreateSut();
        var brokerFailure = new InvalidOperationException("broker unavailable");
        mocks.PublishEndpoint
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<Type>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(brokerFailure));

        Func<Task> act = () => sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(brokerFailure);
    }

    // ── Batch: each event published in order, each under its own runtime type ──
    [Fact]
    public async Task PublishBatch_PublishesEachEventInOrderWithItsOwnRuntimeType()
    {
        var (sut, mocks) = CreateSut();
        var published = new List<(object Message, Type MessageType, CancellationToken Token)>();
        mocks.PublishEndpoint
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<Type>(), It.IsAny<CancellationToken>()))
            .Callback<object, Type, CancellationToken>((m, t, ct) => published.Add((m, t, ct)))
            .Returns(Task.CompletedTask);
        var first = new TestIntegrationEvent();
        var second = new OtherIntegrationEvent();
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync([first, second], cts.Token);

        published.Should().HaveCount(2);
        published[0].Message.Should().BeSameAs(first);
        published[0].MessageType.Should().Be<TestIntegrationEvent>();
        published[1].Message.Should().BeSameAs(second);
        published[1].MessageType.Should().Be<OtherIntegrationEvent>();
        published.Should().AllSatisfy(p => p.Token.Should().Be(cts.Token));
    }

    // ── Batch: sequential publishing stops at the first failure ──
    [Fact]
    public async Task PublishBatch_WhenFirstEventFaults_DoesNotPublishSubsequentEvents()
    {
        var (sut, mocks) = CreateSut();
        var first = new TestIntegrationEvent();
        var second = new OtherIntegrationEvent();
        mocks.PublishEndpoint
            .Setup(p => p.Publish(first, typeof(TestIntegrationEvent), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new InvalidOperationException("broker unavailable")));

        Func<Task> act = () => sut.PublishAsync(new IIntegrationEvent[] { first, second }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        mocks.PublishEndpoint.Verify(
            p => p.Publish(second, typeof(OtherIntegrationEvent), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Empty batch: nothing hits the endpoint ──
    [Fact]
    public async Task PublishBatch_EmptyCollection_DoesNotPublish()
    {
        var (sut, mocks) = CreateSut();

        await sut.PublishAsync(Array.Empty<IIntegrationEvent>(), CancellationToken.None);

        mocks.PublishEndpoint.VerifyNoOtherCalls();
    }

    // ── Header stamping ──

    /// <summary>
    /// Builds a bus over the ambient context a scope would supply, and captures the publish pipe the
    /// stamping path hands MassTransit so a test can run it against a stand-in message.
    /// </summary>
    private static (BrokerMessageBus Sut, Mock<IPublishEndpoint> Endpoint, List<IPipe<PublishContext>> Pipes) CreateStampingSut(
        int? userId = 42,
        string[]? roles = null,
        string? tenantId = "tenant-a",
        string? correlationId = "correlation-1")
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Roles).Returns(roles ?? ["Admin", "Organizer"]);

        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.TenantId).Returns(tenantId);

        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.SetupGet(c => c.CorrelationId).Returns(correlationId!);

        List<IPipe<PublishContext>> pipes = [];
        var publishEndpoint = new Mock<IPublishEndpoint>();
        publishEndpoint
            .Setup(p => p.Publish(
                It.IsAny<object>(),
                It.IsAny<Type>(),
                It.IsAny<IPipe<PublishContext>>(),
                It.IsAny<CancellationToken>()))
            .Callback<object, Type, IPipe<PublishContext>, CancellationToken>((_, _, pipe, _) => pipes.Add(pipe))
            .Returns(Task.CompletedTask);
        publishEndpoint
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<Type>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new BrokerMessageBus(
            publishEndpoint.Object,
            currentUser.Object,
            tenantContext.Object,
            correlationContext.Object);

        return (sut, publishEndpoint, pipes);
    }

    /// <summary>Runs one captured pipe against a stand-in message and returns the headers it wrote.</summary>
    private static async Task<Dictionary<string, string>> HeadersWrittenByAsync(IPipe<PublishContext> pipe)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        var headers = new Mock<SendHeaders>();
        headers.Setup(h => h.Set(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((key, value) => written[key] = value);

        var publishContext = new Mock<PublishContext>();
        publishContext.SetupGet(c => c.Headers).Returns(headers.Object);

        await pipe.Send(publishContext.Object);

        return written;
    }

    [Fact]
    public async Task PublishAsync_StampsTheAmbientContextAsHeaders()
    {
        var (sut, _, pipes) = CreateStampingSut();

        await sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        var written = await HeadersWrittenByAsync(pipes.Should().ContainSingle().Subject);
        written.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageHeaders.TenantId] = "tenant-a",
            [MessageHeaders.UserId] = "42",
            [MessageHeaders.UserRoles] = "Admin,Organizer",
            [MessageHeaders.CorrelationId] = "correlation-1",
        });
    }

    [Fact]
    public async Task PublishAsync_OmitsEveryHeaderWhoseValueIsAbsent()
    {
        // An absent header reads on the consumer side as "the publisher had nothing to say". Writing
        // an empty one would turn a non-answer into a false answer.
        var (sut, _, pipes) = CreateStampingSut(userId: null, roles: [], tenantId: null);

        await sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        var written = await HeadersWrittenByAsync(pipes.Should().ContainSingle().Subject);
        written.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new KeyValuePair<string, string>(MessageHeaders.CorrelationId, "correlation-1"));
    }

    [Fact]
    public async Task PublishAsync_StampsTheRuntimeTypeAlongsideTheHeaders()
    {
        var (sut, endpoint, _) = CreateStampingSut();
        var integrationEvent = new TestIntegrationEvent();

        await sut.PublishAsync(integrationEvent, CancellationToken.None);

        endpoint.Verify(
            p => p.Publish(
                It.Is<object>(m => ReferenceEquals(m, integrationEvent)),
                typeof(TestIntegrationEvent),
                It.IsAny<IPipe<PublishContext>>(),
                CancellationToken.None),
            Times.Once,
            "routing by the concrete class is unchanged by the stamping path");
    }

    [Fact]
    public async Task PublishAsync_WithNothingToStamp_TakesThePlainOverload()
    {
        // A host that resolved no ambient context must not pay a pipe allocation per message for
        // headers it never writes.
        var (sut, mocks) = CreateSut();

        await sut.PublishAsync(new TestIntegrationEvent(), CancellationToken.None);

        mocks.PublishEndpoint.Verify(
            p => p.Publish(It.IsAny<object>(), It.IsAny<Type>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mocks.PublishEndpoint.Verify(
            p => p.Publish(
                It.IsAny<object>(),
                It.IsAny<Type>(),
                It.IsAny<IPipe<PublishContext>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishBatch_StampsEveryMessageOfTheBatch()
    {
        var (sut, _, pipes) = CreateStampingSut();

        await sut.PublishAsync([new TestIntegrationEvent(), new OtherIntegrationEvent()], CancellationToken.None);

        pipes.Should().HaveCount(2);
        foreach (var pipe in pipes)
        {
            var written = await HeadersWrittenByAsync(pipe);
            written.Should().ContainKey(MessageHeaders.CorrelationId)
                .WhoseValue.Should().Be("correlation-1");
        }
    }
}
