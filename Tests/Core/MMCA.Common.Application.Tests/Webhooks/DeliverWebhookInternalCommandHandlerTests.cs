using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <c>DeliverWebhookInternalCommandHandler</c> (rules 35.28 to 35.35): which subscriptions are sent to,
/// what request the sender receives, and how each <see cref="WebhookSendStatus"/> maps onto the
/// subscription's last-outcome columns and onto success (row completes) or failure (processor retries).
/// The handler is built with <see cref="ActivatorUtilities"/> over a container of doubles, so these
/// tests do not depend on its constructor.
/// </summary>
public sealed class DeliverWebhookInternalCommandHandlerTests
{
    private const string Tenant = "tenant-a";
    private const string EventType = "ticket.opened";
    private const string PayloadJson = """{"ticketId":7}""";

    private static readonly Guid EventId = Guid.Parse("3c1d2e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f");
    private static readonly DateTime OccurredOn = new(2026, 11, 12, 8, 59, 30, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly Mock<IWebhookSubscriptionStore> _store = new();
    private readonly Mock<IWebhookSender> _sender = new();
    private readonly Mock<ITenantContext> _tenant = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly List<WebhookDeliveryRequest> _requests = [];

    public DeliverWebhookInternalCommandHandlerTests()
    {
        _tenant.SetupGet(t => t.TenantId).Returns(Tenant);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // -- Nothing to send (rule 35.29) --
    [Fact]
    public async Task MissingSubscription_SucceedsWithoutSending()
    {
        var result = await HandleAsync(Command(Guid.NewGuid()));

        result.IsSuccess.Should().BeTrue("a deleted or unknown subscription completes the row instead of retrying");
        _sender.Verify(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.RecordOutcomeAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OtherTenant_SucceedsWithoutSending()
    {
        // The store filters by tenant (rule 35.12): the row exists only for another tenant.
        var subscription = NewSubscription("tenant-b");
        _store.Setup(s => s.FindAsync(subscription.Id, "tenant-b", It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var result = await HandleAsync(Command(subscription.Id));

        result.IsSuccess.Should().BeTrue();
        _sender.Verify(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadsTheSubscriptionWithTheRestoredTenant()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.Delivered, 204);

        await HandleAsync(Command(subscription.Id));

        _store.Verify(s => s.FindAsync(subscription.Id, Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disabled_SucceedsWithoutSending()
    {
        var subscription = Seed();
        subscription.Disable(Now.AddHours(-1));

        var result = await HandleAsync(Command(subscription.Id));

        result.IsSuccess.Should().BeTrue();
        _sender.Verify(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deleted_SucceedsWithoutSending()
    {
        var subscription = Seed();
        subscription.Delete().IsSuccess.Should().BeTrue();

        var result = await HandleAsync(Command(subscription.Id));

        result.IsSuccess.Should().BeTrue();
        _sender.Verify(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- The request (rules 35.30, 35.31) --
    [Fact]
    public async Task SendsTheEnvelopeToTheSubscriptionUrl_WithTheDeliveryIdAndCurrentSecret()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.Delivered, 200);
        var command = Command(subscription.Id);

        await HandleAsync(command);

        var request = _requests.Should().ContainSingle().Which;
        request.Target.Should().Be(new Uri(subscription.Url));
        request.WebhookId.Should().Be(command.DeliveryId.ToString("D"));
        request.Body.Should().Be(WebhookEnvelope.Build(EventType, OccurredOn, PayloadJson));
        request.ProtectedSecrets.Should().Equal("protected-current");
    }

    [Fact]
    public async Task PassesBothSecretsDuringGrace()
    {
        var subscription = Seed();
        subscription.RotateSecret("protected-next", Now.AddHours(1));
        SendReturns(WebhookSendStatus.Delivered, 200);

        await HandleAsync(Command(subscription.Id));

        _requests.Should().ContainSingle().Which.ProtectedSecrets.Should().Equal("protected-next", "protected-current");
    }

    [Fact]
    public async Task AfterGrace_PassesOnlyTheCurrentSecret()
    {
        var subscription = Seed();
        subscription.RotateSecret("protected-next", Now.AddHours(1));
        _clock.Advance(TimeSpan.FromHours(2));
        SendReturns(WebhookSendStatus.Delivered, 200);

        await HandleAsync(Command(subscription.Id));

        _requests.Should().ContainSingle().Which.ProtectedSecrets.Should().Equal("protected-next");
    }

    // -- Outcome mapping (rules 35.33, 35.34) --
    [Fact]
    public async Task Delivered_RecordsAndSucceeds()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.Delivered, 202);

        var result = await HandleAsync(Command(subscription.Id));

        result.IsSuccess.Should().BeTrue();
        subscription.LastDeliveredOn.Should().Be(Now);
        subscription.IsEnabled().Should().BeTrue();
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Http500_RecordsAndFails()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.Rejected, 500);

        var result = await HandleAsync(Command(subscription.Id));

        result.IsFailure.Should().BeTrue("the processor retries with backoff and dead-letters after MaxAttempts");
        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.DeliveryFailed);
        subscription.LastFailedOn.Should().Be(Now);
        subscription.LastFailure.Should().Be("HTTP 500");
        subscription.IsEnabled().Should().BeTrue();
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Http302_RecordsAndFails()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.Rejected, 302);

        var result = await HandleAsync(Command(subscription.Id));

        result.IsFailure.Should().BeTrue("a redirect is never followed and counts as a rejection");
        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.DeliveryFailed);
        subscription.LastFailure.Should().Be("HTTP 302");
        subscription.LastDeliveredOn.Should().BeNull();
    }

    [Fact]
    public async Task Gone_DisablesAndSucceeds()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.Gone, 410);

        var result = await HandleAsync(Command(subscription.Id));

        result.IsSuccess.Should().BeTrue("410 means the receiver asked to stop; retrying cannot change that");
        subscription.IsEnabled().Should().BeFalse();
        subscription.DisabledOn.Should().Be(Now);
        subscription.LastFailedOn.Should().Be(Now);
        subscription.LastFailure.Should().Be("HTTP 410");
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TargetRefused_RecordsAndSucceeds()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.TargetRefused, null, "WebhookTargetRefusedException: 10.0.0.5 is not a public address");

        var result = await HandleAsync(Command(subscription.Id));

        result.IsSuccess.Should().BeTrue("retrying a refused target cannot change the outcome");
        subscription.LastFailedOn.Should().Be(Now);
        subscription.LastFailure.Should().Contain("10.0.0.5 is not a public address");
        _logs.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(1);
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransportFailed_Fails()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.TransportFailed, null, "TaskCanceledException: The request timed out");

        var result = await HandleAsync(Command(subscription.Id));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.DeliveryFailed);
        subscription.LastFailedOn.Should().Be(Now);
        subscription.LastFailure.Should().Contain("The request timed out");
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecretUnreadable_FailsWithSecretUnreadable()
    {
        var subscription = Seed();
        SendReturns(WebhookSendStatus.SecretUnreadable, null, "CryptographicException: The key was not found in the key ring");

        var result = await HandleAsync(Command(subscription.Id));

        result.IsFailure.Should().BeTrue("a replica with the shared key ring may still read it");
        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.SecretUnreadable);
        subscription.LastFailedOn.Should().Be(Now);
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(WebhookSendStatus.Delivered, 200)]
    [InlineData(WebhookSendStatus.Rejected, 500)]
    [InlineData(WebhookSendStatus.Gone, 410)]
    [InlineData(WebhookSendStatus.TargetRefused, null)]
    [InlineData(WebhookSendStatus.TransportFailed, null)]
    [InlineData(WebhookSendStatus.SecretUnreadable, null)]
    public async Task RecordOutcomeCalledOnce(WebhookSendStatus status, int? httpStatusCode)
    {
        var subscription = Seed();
        SendReturns(status, httpStatusCode, status is WebhookSendStatus.Delivered ? null : "SomeException: detail");

        await HandleAsync(Command(subscription.Id));

        _sender.Verify(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.RecordOutcomeAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Shutdown is not a failure (rule 35.35) --
    [Fact]
    public async Task Cancellation_PropagatesWithoutRecording()
    {
        var subscription = Seed();
        _sender.Setup(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => HandleAsync(Command(subscription.Id));

        await act.Should().ThrowAsync<OperationCanceledException>();
        subscription.LastFailedOn.Should().BeNull();
        _store.Verify(s => s.RecordOutcomeAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DeliverWebhookInternalCommand Command(Guid subscriptionId) =>
        new(subscriptionId, WebhookDeliveryId.For(EventId, subscriptionId), EventType, EventId, OccurredOn, PayloadJson);

    private static WebhookSubscription NewSubscription(string? tenantId) =>
        WebhookSubscription.Create("https://hooks.example.com/inbound", null, [EventType], tenantId, "protected-current").Value!;

    private WebhookSubscription Seed()
    {
        var subscription = NewSubscription(Tenant);
        _store.Setup(s => s.FindAsync(subscription.Id, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        return subscription;
    }

    private void SendReturns(WebhookSendStatus status, int? httpStatusCode, string? detail = null) =>
        _sender.Setup(s => s.SendAsync(It.IsAny<WebhookDeliveryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WebhookDeliveryRequest, CancellationToken>((request, _) => _requests.Add(request))
            .ReturnsAsync(new WebhookSendOutcome(status, httpStatusCode, detail));

    private async Task<Result> HandleAsync(DeliverWebhookInternalCommand command)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton(Options.Create(new WebhookSettings { Enabled = true }));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(_store.Object);
        services.AddSingleton(_sender.Object);
        services.AddSingleton(_tenant.Object);
        await using var provider = services.BuildServiceProvider();

        var handler = ActivatorUtilities.CreateInstance<DeliverWebhookInternalCommandHandler>(provider);
        return await handler.HandleAsync(command, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
