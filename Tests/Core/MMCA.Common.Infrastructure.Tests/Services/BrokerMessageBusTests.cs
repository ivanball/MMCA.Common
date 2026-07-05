#pragma warning disable CA2263 // The SUT deliberately calls the non-generic Publish(object, Type, CancellationToken) overload; mock setups and verifies must match that exact overload.

using AwesomeAssertions;
using MassTransit;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

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
}
