using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Messaging.Consumers;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Shared.Messaging;
using Moq;

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

    private static Dictionary<string, string> HeadersFor(
        string? tenantId = null,
        string? userId = null,
        string? userRoles = null,
        string? correlationId = null)
    {
        Dictionary<string, string> headers = [];
        AddIfPresent(headers, MessageHeaders.TenantId, tenantId);
        AddIfPresent(headers, MessageHeaders.UserId, userId);
        AddIfPresent(headers, MessageHeaders.UserRoles, userRoles);
        AddIfPresent(headers, MessageHeaders.CorrelationId, correlationId);
        return headers;
    }

    private static void AddIfPresent(Dictionary<string, string> headers, string name, string? value)
    {
        if (value is not null)
        {
            headers[name] = value;
        }
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

        await sut.ConsumeAsync(evt, HeadersFor("tenant-a", "42", "Admin,Manager", "correlation-1"), CancellationToken.None);

        scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId.Should().Be("tenant-a");
        scope.ServiceProvider.GetRequiredService<ICorrelationContext>().CorrelationId.Should().Be("correlation-1");

        var principal = scope.ServiceProvider.GetRequiredService<ScopedUserOverride>().Principal;
        principal.Should().NotBeNull();
        principal!.Identity!.IsAuthenticated.Should().BeTrue("an unauthenticated principal would be denied every permission check");
        principal.IsInRole("Admin").Should().BeTrue();
        principal.IsInRole("Manager").Should().BeTrue();
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

        await sut.ConsumeAsync(evt, HeadersFor(tenantId: "tenant-b"), CancellationToken.None);

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

        await sut.ConsumeAsync(evt, HeadersFor(), CancellationToken.None);

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

        await sut.ConsumeAsync(evt, HeadersFor(correlationId: "correlation-9"), CancellationToken.None);

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

        await sut.ConsumeAsync(evt, HeadersFor(userId: "not-a-number"), CancellationToken.None);

        scope.ServiceProvider.GetRequiredService<ScopedUserOverride>().Principal.Should().BeNull();
        inbox.Verify(
            x => x.CompleteAsync(evt.MessageId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
