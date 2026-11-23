using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Messaging;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Messaging;

/// <summary>
/// Tests for <see cref="BrokerMessageBus"/>: the runtime type decides the topic, the wire name and the
/// body; the <see cref="BrokerHeaders"/> transport headers and the <see cref="MessageHeaders"/> origin
/// headers are stamped; the W3C trace context travels as headers; and a publisher failure surfaces
/// unwrapped to the outbox processor.
/// </summary>
public sealed class BrokerMessageBusTests
{
    private const string Namespace = "bus-tests";

    public sealed record class TestIntegrationEvent(string Sku = "sku-1") : BaseIntegrationEvent;

    [EventName("Orders.OrderShipped.v1")]
    public sealed record class OtherIntegrationEvent : BaseIntegrationEvent;

    private static BrokerTopology Topology { get; } = new(Namespace, "bus-tests-queue", []);

    private static (BrokerMessageBus Sut, List<OutgoingBrokerMessage> Published, Mock<IBrokerPublisher> Publisher) CreateSut(
        ICurrentUserService? currentUser = null,
        ITenantContext? tenantContext = null,
        ICorrelationContext? correlationContext = null)
    {
        List<OutgoingBrokerMessage> published = [];
        var publisher = new Mock<IBrokerPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<OutgoingBrokerMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingBrokerMessage, CancellationToken>((message, _) => published.Add(message))
            .Returns(Task.CompletedTask);

        return (new BrokerMessageBus(publisher.Object, Topology, currentUser, tenantContext, correlationContext), published, publisher);
    }

    private static BrokerMessageBus CreateStampingSut(
        List<OutgoingBrokerMessage> published,
        int? userId = 42,
        string[]? roles = null,
        string? tenantId = "tenant-a",
        string? correlationId = "correlation-1",
        ClaimsPrincipal? user = null)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Roles).Returns(roles ?? ["Admin", "Manager"]);
        if (user is not null)
        {
            currentUser.SetupGet(u => u.User).Returns(user);
        }

        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(t => t.TenantId).Returns(tenantId);

        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.SetupGet(c => c.CorrelationId).Returns(correlationId!);

        var publisher = new Mock<IBrokerPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<OutgoingBrokerMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingBrokerMessage, CancellationToken>((message, _) => published.Add(message))
            .Returns(Task.CompletedTask);

        return new BrokerMessageBus(publisher.Object, Topology, currentUser.Object, tenantContext.Object, correlationContext.Object);
    }

    [Fact]
    public async Task PublishAsync_NullEvent_ThrowsArgumentNullException()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IIntegrationEvent)null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("integrationEvent");
    }

    [Fact]
    public async Task PublishAsync_NullEventCollection_ThrowsArgumentNullException()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.PublishAsync((IEnumerable<IIntegrationEvent>)null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("integrationEvents");
    }

    [Fact]
    public async Task PublishAsync_PublishesOnTheRuntimeTypesTopic_WithTheTransportHeaders()
    {
        var (sut, published, _) = CreateSut();
        var integrationEvent = new TestIntegrationEvent("sku-9");

        await sut.PublishAsync(integrationEvent, TestContext.Current.CancellationToken);

        var message = published.Should().ContainSingle().Subject;
        var eventName = BrokerNaming.EventName(typeof(TestIntegrationEvent));
        message.EventName.Should().Be(eventName);
        message.Topic.Should().Be(BrokerNaming.Topic(Namespace, eventName));
        message.MessageId.Should().Be(integrationEvent.MessageId);
        message.Headers[BrokerHeaders.EventName].Should().Be(eventName);
        message.Headers[BrokerHeaders.MessageId].Should().Be(integrationEvent.MessageId.ToString("D"));
        message.Headers[BrokerHeaders.ContentType].Should().Be(BrokerHeaders.JsonContentType);
        message.Headers.Should().NotContainKey(BrokerHeaders.RedeliveryCount, "a first delivery carries no count");
    }

    [Fact]
    public async Task PublishAsync_BodyIsTheJsonOfTheRuntimeType_AndRoundTrips()
    {
        var (sut, published, _) = CreateSut();
        var integrationEvent = new TestIntegrationEvent("sku-round-trip");

        await sut.PublishAsync(integrationEvent, TestContext.Current.CancellationToken);

        var body = published.Should().ContainSingle().Subject.Body;
        var read = JsonSerializer.Deserialize<TestIntegrationEvent>(body.Span);
        read.Should().NotBeNull();
        read!.Sku.Should().Be("sku-round-trip");
        read.MessageId.Should().Be(integrationEvent.MessageId);
    }

    [Fact]
    public async Task PublishAsync_ADeclaredEventName_DecidesTheTopic()
    {
        var (sut, published, _) = CreateSut();

        await sut.PublishAsync(new OtherIntegrationEvent(), TestContext.Current.CancellationToken);

        published.Should().ContainSingle().Which.Topic.Should().Be("bus-tests-orders-order-shipped-v1");
    }

    [Fact]
    public async Task PublishAsync_ForwardsTheTokenAndSurfacesAPublisherFailureUnwrapped()
    {
        var (sut, _, publisher) = CreateSut();
        using var cts = new CancellationTokenSource();
        var failure = new InvalidOperationException("broker refused");
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<OutgoingBrokerMessage>(), cts.Token))
            .ThrowsAsync(failure);

        Func<Task> act = () => sut.PublishAsync(new TestIntegrationEvent(), cts.Token);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task PublishAsync_Batch_PublishesEachEventInOrder()
    {
        var (sut, published, _) = CreateSut();
        var first = new TestIntegrationEvent("a");
        var second = new OtherIntegrationEvent();

        await sut.PublishAsync([first, second], TestContext.Current.CancellationToken);

        published.Select(m => m.MessageId).Should().Equal(first.MessageId, second.MessageId);
    }

    [Fact]
    public async Task PublishAsync_StampsTheAmbientContextAsHeaders()
    {
        List<OutgoingBrokerMessage> published = [];
        var sut = CreateStampingSut(published);

        await sut.PublishAsync(new TestIntegrationEvent(), TestContext.Current.CancellationToken);

        var headers = published.Should().ContainSingle().Subject.Headers;
        headers[MessageHeaders.TenantId].Should().Be("tenant-a");
        headers[MessageHeaders.UserId].Should().Be("42");
        headers[MessageHeaders.UserRoles].Should().Be("Admin,Manager");
        headers[MessageHeaders.CorrelationId].Should().Be("correlation-1");
    }

    [Fact]
    public async Task PublishAsync_OmitsEveryOriginHeaderWhoseValueIsAbsent()
    {
        // An absent header reads on the consumer side as "the publisher had nothing to say". Writing
        // an empty one would turn a non-answer into a false answer.
        List<OutgoingBrokerMessage> published = [];
        var sut = CreateStampingSut(published, userId: null, roles: [], tenantId: null);

        await sut.PublishAsync(new TestIntegrationEvent(), TestContext.Current.CancellationToken);

        var headers = published.Should().ContainSingle().Subject.Headers;
        headers.Should().NotContainKey(MessageHeaders.TenantId);
        headers.Should().NotContainKey(MessageHeaders.UserId);
        headers.Should().NotContainKey(MessageHeaders.UserRoles);
        headers[MessageHeaders.CorrelationId].Should().Be("correlation-1");
    }

    // L181: a publish authorized ONLY through a Permission claim stamps the claims as a header, and
    // the consumer's restore rebuilds them, so the consuming scope passes the same gate.
    [Fact]
    public async Task PublishThenConsume_APrincipalGrantedOnlyByAPermissionClaim_PassesTheSameGateDeferred()
    {
        var principal = DeferredPermissionGate.ClaimOnlyPrincipal(42);
        (await DeferredPermissionGate.EvaluateAsync(new DeferredPermissionGate.FixedCurrentUserService(principal)))
            .IsSuccess.Should().BeTrue("control: the publishing request is authorized by the claim alone");

        List<OutgoingBrokerMessage> published = [];
        var sut = CreateStampingSut(published, roles: [], user: principal);
        await sut.PublishAsync(new TestIntegrationEvent(), TestContext.Current.CancellationToken);
        var headers = published.Should().ContainSingle().Subject.Headers;

        await using var provider = new ServiceCollection().AddScoped<ScopedUserOverride>().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using (ConsumerOriginRestore.Apply(headers, scope.ServiceProvider, "Broker"))
        {
            var consumingUser = new ImpersonatingCurrentUserService(
                new DeferredPermissionGate.FixedCurrentUserService(new ClaimsPrincipal()),
                scope.ServiceProvider.GetRequiredService<ScopedUserOverride>());

            (await DeferredPermissionGate.EvaluateAsync(consumingUser)).IsSuccess.Should().BeTrue(
                "the consuming scope must carry the Permission claims the publishing gate granted on");
        }
    }

    [Fact]
    public async Task PublishAsync_WithAListener_StartsAProducerSpanAndPropagatesIt()
    {
        var (sut, published, _) = CreateSut();
        List<Activity> started = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MMCA.Common.Messaging",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (started)
                {
                    started.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var integrationEvent = new TestIntegrationEvent();

        await sut.PublishAsync(integrationEvent, TestContext.Current.CancellationToken);

        var message = published.Should().ContainSingle().Subject;
        Activity producer;
        lock (started)
        {
            producer = started.Should().ContainSingle(a =>
                a.Kind == ActivityKind.Producer
                && Equals(a.GetTagItem("messaging.message.id"), integrationEvent.MessageId.ToString("D"))).Subject;
        }

        message.Headers[BrokerHeaders.TraceParent].Should().Be(producer.Id);
        producer.GetTagItem("messaging.destination.name").Should().Be(message.Topic);
    }

    [Fact]
    public async Task PublishAsync_WithNoListener_PropagatesTheAmbientTraceContext()
    {
        var (sut, published, _) = CreateSut();
        using var ambient = new Activity("outbox-row").SetIdFormat(ActivityIdFormat.W3C);
        ambient.TraceStateString = "vendor=1";
        ambient.Start();

        await sut.PublishAsync(new TestIntegrationEvent(), TestContext.Current.CancellationToken);

        var headers = published.Should().ContainSingle().Subject.Headers;
        headers[BrokerHeaders.TraceParent].Should().Contain(ambient.TraceId.ToHexString());
        headers[BrokerHeaders.TraceState].Should().Be("vendor=1");
    }
}
