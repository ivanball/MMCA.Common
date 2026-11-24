using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Outbox.Processing;

/// <summary>
/// A row that carries the original request's trace context is delivered inside an
/// <c>OutboxProcess</c> consumer span parented on that context and tagged with the row's identity,
/// so the asynchronous hop stays on the request's trace. A row written without a trace context (or
/// with only half of one) gets no span and is still delivered.
/// <para>
/// The parent is rebuilt with the row's STORED trace flags, so under a parent-based sampler (the
/// listener below) the span is recorded only when the raising request was sampled; a row with no
/// stored flags (written before the column existed) reads as not sampled.
/// </para>
/// <para>
/// The <c>MMCA.Common.Outbox</c> source is process-wide, so other tests in this assembly emit spans
/// on it too: every assertion filters by this test's own message id.
/// </para>
/// </summary>
public sealed class OutboxProcessorTracingTests : IDisposable
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string SpanId = "00f067aa0ba902b7";

    private readonly SqliteConnection _connection;
    private readonly TracingTestDbContext _dbContext;
    private readonly Mock<IDomainEventDispatcher> _dispatcher = new();
    private readonly OutboxProcessor _sut;
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _spans = new();

    public OutboxProcessorTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "MMCA.Common.Outbox", StringComparison.Ordinal),
            // Parent-based, as the OpenTelemetry default sampler is: a root follows the listener, a
            // child follows its parent's recorded flag (PropagationData still creates the activity,
            // so an unsampled delivery is observable here as a span that is not recorded).
            Sample = static (ref options) =>
                options.Parent == default || options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.PropagationData,
            ActivityStopped = activity => _spans.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var contextServices = new ServiceCollection();
        contextServices.AddSingleton(TimeProvider.System);
        contextServices.AddSingleton(_dispatcher.Object);
        contextServices.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        contextServices.AddSingleton(new DomainEventSaveChangesInterceptor(
            _dispatcher.Object, NullLogger<DomainEventSaveChangesInterceptor>.Instance, Mock.Of<IOutboxSignal>(), timeProvider: TimeProvider.System));
        contextServices.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        ServiceProvider contextProvider = contextServices.BuildServiceProvider();

        _dbContext = new TracingTestDbContext(
            new DbContextOptionsBuilder<TracingTestDbContext>().UseSqlite(_connection).Options,
            contextProvider,
            Mock.Of<IEntityConfigurationAssemblyProvider>(p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>()));
        _dbContext.Database.EnsureCreated();

        var factory = new Mock<IDbContextFactory>();
        factory.Setup(f => f.GetDbContext(DataSourceKey.Default(DataSource.SQLServer))).Returns(_dbContext);

        var services = new ServiceCollection();
        services.AddSingleton(factory.Object);
        services.AddSingleton(_dispatcher.Object);
        services.AddSingleton(Mock.Of<IMessageBus>());
        ServiceProvider rootProvider = services.BuildServiceProvider();

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);
        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        _sut = new OutboxProcessor(
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(new OutboxSettings()),
            Mock.Of<IOutboxSignal>(),
            new FrameworkTableTargets(registry.Object, resolver.Object),
            TimeProvider.System);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public async Task RowWithATraceContext_IsDeliveredInsideAConsumerSpanParentedOnThatContext()
    {
        ActivityTraceId? traceSeenByHandler = null;
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => traceSeenByHandler = Activity.Current?.TraceId)
            .Returns(Task.CompletedTask);
        var message = await SeedAsync(TraceId, SpanId, (byte)ActivityTraceFlags.Recorded);

        await _sut.ProcessPendingMessagesAsync(TestContext.Current.CancellationToken);

        var span = SpansFor(message.Id).Should().ContainSingle().Which;
        span.Recorded.Should().BeTrue("the raising request was sampled");
        span.HasRemoteParent.Should().BeTrue("the parent was rebuilt from the row, not taken from this process");
        span.Kind.Should().Be(ActivityKind.Consumer);
        span.TraceId.ToHexString().Should().Be(TraceId, "the hop continues the original request's trace");
        span.ParentSpanId.ToHexString().Should().Be(SpanId);
        span.GetTagItem("messaging.outbox.event_type").Should().Be(message.EventType);
        span.GetTagItem("messaging.outbox.data_source").Should().Be(DataSourceKey.Default(DataSource.SQLServer).ToString());
        traceSeenByHandler.Should().NotBeNull();
        traceSeenByHandler!.Value.ToHexString().Should().Be(TraceId, "the handler runs inside the consumer span");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(TraceId, null)]
    [InlineData(null, SpanId)]
    [InlineData("", SpanId)]
    [InlineData(TraceId, "")]
    public async Task RowWithoutACompleteTraceContext_GetsNoSpan_AndIsStillDelivered(string? traceId, string? spanId)
    {
        var message = await SeedAsync(traceId, spanId);

        await _sut.ProcessPendingMessagesAsync(TestContext.Current.CancellationToken);

        SpansFor(message.Id).Should().BeEmpty("a row with no captured trace context has no parent to continue");
        _dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        (await _dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken))
            .ProcessedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task RowFromASampledRequest_IsDeliveredInsideARecordedSpan()
    {
        var message = await SeedAsync(TraceId, SpanId, (byte)ActivityTraceFlags.Recorded);

        await _sut.ProcessPendingMessagesAsync(TestContext.Current.CancellationToken);

        SpansFor(message.Id).Should().ContainSingle().Which.Recorded.Should().BeTrue();
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData(null)]
    public async Task RowFromAnUnsampledRequestOrWithNoStoredFlags_GetsNoRecordedSpan_AndIsStillDelivered(byte? traceFlags)
    {
        var message = await SeedAsync(TraceId, SpanId, traceFlags);

        await _sut.ProcessPendingMessagesAsync(TestContext.Current.CancellationToken);

        SpansFor(message.Id).Should().ContainSingle().Which.Recorded.Should().BeFalse(
            "a parent-based sampler drops the delivery span of a request it did not sample, so no orphan trace is exported");
        _dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        (await _dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken))
            .ProcessedOn.Should().NotBeNull();
    }

    private Activity[] SpansFor(Guid messageId) =>
    [
        .. _spans.Where(a =>
            string.Equals(a.OperationName, "OutboxProcess", StringComparison.Ordinal)
            && string.Equals(a.GetTagItem("messaging.outbox.message_id") as string, messageId.ToString(), StringComparison.Ordinal)),
    ];

    private async Task<OutboxMessage> SeedAsync(string? traceId, string? spanId, byte? traceFlags = null)
    {
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = typeof(TracingTestEvent).AssemblyQualifiedName!,
            Payload = """{"DateOccurred":"2025-01-01T00:00:00Z"}""",
            OccurredOn = DateTime.UtcNow.AddMinutes(-5),
            ProcessedOn = null,
            RetryCount = 0,
            TraceId = traceId,
            SpanId = spanId,
            TraceFlags = traceFlags,
        };
        await _dbContext.Set<OutboxMessage>().AddAsync(message, TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return message;
    }

    public sealed class TracingTestEvent : IDomainEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>SQLite context mapping only <see cref="OutboxMessage"/>.</summary>
    private sealed class TracingTestDbContext(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, TestPhysicalDataSources.Sqlite())
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.ToTable("OutboxMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Payload).IsRequired();
                entity.Property(e => e.LastError).HasMaxLength(4000);
            });
    }
}
