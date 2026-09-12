using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using Moq;

// Aliased for the same reason the consumer itself aliases it: MassTransit ships its own static
// MessageHeaders, so the bare name is ambiguous here.
using MessageHeaders = MMCA.Common.Shared.Messaging.MessageHeaders;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Consumers;

/// <summary>
/// The consumer half of the context propagation contract: <c>BrokerMessageBus</c> stamps the
/// publisher's identity, tenant and correlation id as headers, and this consumer restores them onto
/// its own scope before anything reads it. Without the restore, every broker-delivered handler ran
/// anonymous, tenant-less and uncorrelated, which the in-process path never did.
/// </summary>
public sealed class IntegrationEventConsumerContextTests
{
    public sealed record class TestIntegrationEvent : BaseIntegrationEvent;

    /// <summary>
    /// A scope carrying exactly the three services <c>AddInfrastructure</c> registers for ambient
    /// context, so the restore is exercised against the real implementations rather than mocks.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICorrelationContext, CorrelationContext>();
        services.AddScoped<ScopedUserOverride>();
        return services.BuildServiceProvider();
    }

    private static Mock<ConsumeContext<TestIntegrationEvent>> ContextFor(
        TestIntegrationEvent evt,
        string? tenantId = null,
        string? userId = null,
        string? userRoles = null,
        string? correlationId = null)
    {
        var headers = new Mock<Headers>();
        headers.Setup(h => h.Get<string>(MessageHeaders.TenantId, null)).Returns(tenantId);
        headers.Setup(h => h.Get<string>(MessageHeaders.UserId, null)).Returns(userId);
        headers.Setup(h => h.Get<string>(MessageHeaders.UserRoles, null)).Returns(userRoles);
        headers.Setup(h => h.Get<string>(MessageHeaders.CorrelationId, null)).Returns(correlationId);

        var context = new Mock<ConsumeContext<TestIntegrationEvent>>();
        context.SetupGet(c => c.Message).Returns(evt);
        context.SetupGet(c => c.Headers).Returns(headers.Object);
        return context;
    }

    private static Mock<IInboxStore> InboxFor(TestIntegrationEvent evt)
    {
        var inbox = new Mock<IInboxStore>();
        inbox.Setup(x => x.TryBeginAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return inbox;
    }

    private static IntegrationEventConsumer<TestIntegrationEvent> CreateSut(
        IServiceProvider services,
        IInboxStore inbox,
        params IIntegrationEventHandler<TestIntegrationEvent>[] handlers) =>
        new(
            handlers,
            inbox,
            NullLogger<IntegrationEventConsumer<TestIntegrationEvent>>.Instance,
            services);

    [Fact]
    public async Task Consume_RestoresTenantUserRolesAndCorrelationFromTheHeaders()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evt = new TestIntegrationEvent();

        var sut = CreateSut(scope.ServiceProvider, InboxFor(evt).Object);

        await sut.Consume(ContextFor(evt, "tenant-a", "42", "Admin,Organizer", "correlation-1").Object);

        scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId.Should().Be("tenant-a");
        scope.ServiceProvider.GetRequiredService<ICorrelationContext>().CorrelationId.Should().Be("correlation-1");

        var principal = scope.ServiceProvider.GetRequiredService<ScopedUserOverride>().Principal;
        principal.Should().NotBeNull();
        principal!.Identity!.IsAuthenticated.Should().BeTrue("an unauthenticated principal would be denied every permission check");
        principal.IsInRole("Admin").Should().BeTrue();
        principal.IsInRole("Organizer").Should().BeTrue();
    }

    [Fact]
    public async Task Consume_RestoresTheContextBeforeTheInboxIsTouched()
    {
        // Load-bearing ordering: under database-per-tenant the inbox store resolves its context
        // through the scoped factory, and that routing is decided by the tenant restored here.
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evt = new TestIntegrationEvent();

        string? tenantAtInbox = null;
        var inbox = InboxFor(evt);
        inbox.Setup(x => x.TryBeginAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => tenantAtInbox = scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId)
            .ReturnsAsync(true);

        var sut = CreateSut(scope.ServiceProvider, inbox.Object);

        await sut.Consume(ContextFor(evt, tenantId: "tenant-b").Object);

        tenantAtInbox.Should().Be("tenant-b");
    }

    [Fact]
    public async Task Consume_LeavesTheDefaultsUntouched_WhenTheMessageCarriesNoHeaders()
    {
        // An older publisher, or an event raised with no user, carries nothing. That must read as
        // "the publisher had nothing to say", not as a captured anonymous identity.
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evt = new TestIntegrationEvent();

        var sut = CreateSut(scope.ServiceProvider, InboxFor(evt).Object);

        await sut.Consume(ContextFor(evt).Object);

        scope.ServiceProvider.GetRequiredService<ITenantContext>().IsResolved.Should().BeFalse();
        scope.ServiceProvider.GetRequiredService<ScopedUserOverride>().Principal.Should().BeNull();
    }

    [Fact]
    public async Task Consume_RunsTheHandlersUnderTheRestoredContext()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evt = new TestIntegrationEvent();

        string? correlationInHandler = null;
        var handler = new Mock<IIntegrationEventHandler<TestIntegrationEvent>>();
        handler.Setup(h => h.HandleAsync(evt, It.IsAny<CancellationToken>()))
            .Callback(() => correlationInHandler =
                scope.ServiceProvider.GetRequiredService<ICorrelationContext>().CorrelationId)
            .Returns(Task.CompletedTask);

        var sut = CreateSut(scope.ServiceProvider, InboxFor(evt).Object, handler.Object);

        await sut.Consume(ContextFor(evt, correlationId: "correlation-9").Object);

        correlationInHandler.Should().Be("correlation-9");
    }

    [Fact]
    public async Task Consume_IgnoresAUserIdHeaderThatIsNotAnIdentifier()
    {
        // A malformed header is a publisher bug, not a reason to fail the consume: the message is
        // still delivered, simply without an identity.
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evt = new TestIntegrationEvent();
        var inbox = InboxFor(evt);

        var sut = CreateSut(scope.ServiceProvider, inbox.Object);

        await sut.Consume(ContextFor(evt, userId: "not-a-number").Object);

        scope.ServiceProvider.GetRequiredService<ScopedUserOverride>().Principal.Should().BeNull();
        inbox.Verify(
            x => x.CompleteAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
