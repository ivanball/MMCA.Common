using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <c>WebhookFanOutHandler&lt;TEvent&gt;</c> (rules 35.5, 35.12, 35.25 to 35.27): the handler is resolved
/// exactly as a host gets it, through <c>AddWebhookEvent</c>, so these tests do not depend on its
/// constructor. Every scoped dependency is a double registered in the container the handler's own
/// scope is opened from.
/// </summary>
public sealed class WebhookFanOutHandlerTests
{
    private const string EventType = "ticket.opened";
    private const string Tenant = "tenant-a";

    private readonly Mock<IWebhookSubscriptionStore> _store = new();
    private readonly Mock<ITenantContext> _tenant = new();
    private readonly TransactionRecordingUnitOfWork _unitOfWork = new();
    private readonly RecordingScheduler _scheduler;
    private readonly RecordingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));

    public WebhookFanOutHandlerTests()
    {
        _scheduler = new RecordingScheduler(_unitOfWork);
        _tenant.SetupGet(t => t.TenantId).Returns(Tenant);
    }

    // -- Inert unless enabled (rule 35.5) --
    [Fact]
    public async Task SettingsNotRegistered_NeverTouchesStore()
    {
        var services = new ServiceCollection();
        services.AddWebhookEvent<WebhookFanOutProbeEvent>(EventType);

        // No AddLogging and no AddOptions: IOptions<WebhookSettings> is not resolvable at all.
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        AddScopedDoubles(services);
        await using var provider = services.BuildServiceProvider();

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _store.VerifyNoOtherCalls();
        _unitOfWork.TransactionCount.Should().Be(0);
        _scheduler.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_NeverTouchesStore()
    {
        await using var provider = BuildProvider(new WebhookSettings { Enabled = false });
        SeedSubscriptions(NewSubscription([EventType]));

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _store.VerifyNoOtherCalls();
        _unitOfWork.TransactionCount.Should().Be(0);
        _scheduler.Scheduled.Should().BeEmpty();
    }

    // -- Fan-out (rules 35.25, 35.26) --
    [Fact]
    public async Task SchedulesOneCommandPerMatchingSubscription()
    {
        var listed = NewSubscription([EventType]);
        var wildcard = NewSubscription(["*"]);
        var other = NewSubscription(["ticket.closed"]);
        SeedSubscriptions(listed, wildcard, other);
        await using var provider = BuildProvider(Enabled());

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _scheduler.Scheduled.Select(s => s.Command).OfType<DeliverWebhookInternalCommand>()
            .Select(c => c.SubscriptionId)
            .Should().BeEquivalentTo(new[] { listed.Id, wildcard.Id }, "the listed type and the wildcard match, the other type does not");
        _scheduler.Scheduled.Should().HaveCount(2);
    }

    [Fact]
    public async Task ScheduledCommand_CarriesTheEventIdentity_AndTheWebSerializedPayload()
    {
        var subscription = NewSubscription([EventType]);
        SeedSubscriptions(subscription);
        await using var provider = BuildProvider(Enabled());
        var integrationEvent = NewEvent();

        await ResolveHandler(provider).HandleAsync(integrationEvent, TestContext.Current.CancellationToken);

        var command = _scheduler.Scheduled.Should().ContainSingle().Which.Command.Should().BeOfType<DeliverWebhookInternalCommand>().Which;
        command.SubscriptionId.Should().Be(subscription.Id);
        command.EventType.Should().Be(EventType, "the webhook name the event was registered under, not its CLR name");
        command.EventId.Should().Be(integrationEvent.MessageId);
        command.OccurredOn.Should().Be(integrationEvent.DateOccurred);
        command.PayloadJson.Should().Be(
            JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task PassesAmbientTenantToStore()
    {
        SeedSubscriptions(NewSubscription([EventType]));
        await using var provider = BuildProvider(Enabled());

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _store.Verify(s => s.ListEnabledAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.ListEnabledAsync(It.Is<string?>(t => t != Tenant), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithNoTenant_ListsTheUntenantedSubscriptions()
    {
        _tenant.SetupGet(t => t.TenantId).Returns((string?)null);
        SeedSubscriptions(NewSubscription([EventType]));
        await using var provider = BuildProvider(Enabled());

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _store.Verify(s => s.ListEnabledAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UsesDeterministicDeliveryId()
    {
        var subscription = NewSubscription([EventType]);
        SeedSubscriptions(subscription);
        await using var provider = BuildProvider(Enabled());
        var integrationEvent = NewEvent();
        var handler = ResolveHandler(provider);

        await handler.HandleAsync(integrationEvent, TestContext.Current.CancellationToken);
        await handler.HandleAsync(integrationEvent, TestContext.Current.CancellationToken);

        var ids = _scheduler.Scheduled.Select(s => ((DeliverWebhookInternalCommand)s.Command).DeliveryId).ToList();
        ids.Should().HaveCount(2);
        ids.Should().OnlyContain(id => id == WebhookDeliveryId.For(integrationEvent.MessageId, subscription.Id), "a redelivered event schedules the same webhook-id");
    }

    [Fact]
    public async Task AllSchedulesInOneTransaction()
    {
        SeedSubscriptions(NewSubscription([EventType]), NewSubscription([EventType]), NewSubscription(["*"]));
        await using var provider = BuildProvider(Enabled());

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _unitOfWork.TransactionCount.Should().Be(1);
        _scheduler.Scheduled.Should().HaveCount(3);
        _scheduler.Scheduled.Should().OnlyContain(s => s.InTransaction, "every row is written inside the one transaction");
    }

    [Fact]
    public async Task NoMatches_OpensNoTransaction()
    {
        SeedSubscriptions(NewSubscription(["ticket.closed"]));
        await using var provider = BuildProvider(Enabled());

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _unitOfWork.TransactionCount.Should().Be(0);
        _scheduler.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task NoSubscriptions_OpensNoTransaction()
    {
        SeedSubscriptions();
        await using var provider = BuildProvider(Enabled());

        await ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        _unitOfWork.TransactionCount.Should().Be(0);
        _scheduler.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task PayloadOverLimit_SchedulesNothingAndWarns()
    {
        SeedSubscriptions(NewSubscription([EventType]));
        await using var provider = BuildProvider(new WebhookSettings { Enabled = true, MaxPayloadBytes = 1024 });
        var integrationEvent = new WebhookFanOutProbeEvent(7, new string('x', 2000));
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(integrationEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .Should().BeGreaterThan(1024, "the precondition of this test");

        var act = () => ResolveHandler(provider).HandleAsync(integrationEvent, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("an oversized event is skipped, not redelivered forever");
        _scheduler.Scheduled.Should().BeEmpty();
        _unitOfWork.TransactionCount.Should().Be(0);
        _logs.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public async Task PayloadUnderLimit_IsScheduled()
    {
        SeedSubscriptions(NewSubscription([EventType]));
        await using var provider = BuildProvider(new WebhookSettings { Enabled = true, MaxPayloadBytes = 1024 });

        await ResolveHandler(provider).HandleAsync(new WebhookFanOutProbeEvent(7, "short"), TestContext.Current.CancellationToken);

        _scheduler.Scheduled.Should().ContainSingle();
        _logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    // -- Failure propagates so the event is redelivered (rule 35.27) --
    [Fact]
    public async Task ScheduleFailure_Throws()
    {
        SeedSubscriptions(NewSubscription([EventType]), NewSubscription([EventType]));
        _scheduler.FailOnCallNumber = 2;
        await using var provider = BuildProvider(Enabled());

        var act = () => ResolveHandler(provider).HandleAsync(NewEvent(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Exception>("the outbox or broker must redeliver the event");
        _unitOfWork.TransactionCount.Should().Be(1);
    }

    private static WebhookSettings Enabled() => new() { Enabled = true };

    private static WebhookFanOutProbeEvent NewEvent() => new(7, "Printer on fire");

    private static IIntegrationEventHandler<WebhookFanOutProbeEvent> ResolveHandler(IServiceProvider provider) =>
        provider.GetServices<IIntegrationEventHandler<WebhookFanOutProbeEvent>>().Should().ContainSingle().Which;

    private static WebhookSubscription NewSubscription(string[] eventTypes) =>
        WebhookSubscription.Create("https://hooks.example.com/" + Guid.NewGuid().ToString("N"), null, eventTypes, Tenant, "protected").Value!;

    private void SeedSubscriptions(params WebhookSubscription[] subscriptions) =>
        _store.Setup(s => s.ListEnabledAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscriptions);

    private ServiceProvider BuildProvider(WebhookSettings settings)
    {
        var services = new ServiceCollection();
        services.AddWebhookEvent<WebhookFanOutProbeEvent>(EventType);
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton(Options.Create(settings));
        AddScopedDoubles(services);
        return services.BuildServiceProvider();
    }

    private void AddScopedDoubles(IServiceCollection services)
    {
        services.AddScoped(_ => _store.Object);
        services.AddScoped(_ => _tenant.Object);
        services.AddScoped<IUnitOfWork>(_ => _unitOfWork);
        services.AddScoped<IInternalCommandScheduler>(_ => _scheduler);
        services.AddSingleton<TimeProvider>(_clock);
    }

    /// <summary>Counts transactions and exposes whether one is open, so schedules can be checked against it.</summary>
    private sealed class TransactionRecordingUnitOfWork : IUnitOfWork
    {
        public int TransactionCount { get; private set; }

        public bool InTransaction { get; private set; }

        IRepository<TEntity, TIdentifierType> IUnitOfWork.GetRepository<TEntity, TIdentifierType>() =>
            throw new NotSupportedException("The fan-out does not read repositories.");

        IReadRepository<TEntity, TIdentifierType> IUnitOfWork.GetReadRepository<TEntity, TIdentifierType>() =>
            throw new NotSupportedException("The fan-out does not read repositories.");

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public void RequestExplicitKeyInsert()
        {
        }

        public async Task<TResult> ExecuteInTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            InTransaction = true;
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                InTransaction = false;
            }
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records every scheduled command, through either overload, and whether a transaction was open.</summary>
    private sealed class RecordingScheduler(TransactionRecordingUnitOfWork unitOfWork) : IInternalCommandScheduler
    {
        public ConcurrentQueue<(IInternalCommand Command, bool InTransaction)> Scheduled { get; } = new();

        public int FailOnCallNumber { get; set; }

        public Task<Result<Guid>> ScheduleAsync(IInternalCommand command, DateTimeOffset? runAt = null, CancellationToken cancellationToken = default) =>
            Record(command);

        public Task<Result<Guid>> ScheduleAsync(IInternalCommand command, TimeSpan delay, CancellationToken cancellationToken = default) =>
            Record(command);

        private Task<Result<Guid>> Record(IInternalCommand command)
        {
            Scheduled.Enqueue((command, unitOfWork.InTransaction));
            return Task.FromResult(Scheduled.Count == FailOnCallNumber
                ? Result.Failure<Guid>(Error.Failure("Test.ScheduleFailed", "The row could not be written."))
                : Result.Success(Guid.NewGuid()));
        }
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

// -- Test types --
public sealed record WebhookFanOutProbeEvent(int TicketId, string Title) : BaseIntegrationEvent;
