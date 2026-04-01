using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Infrastructure.Settings;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class InProcessEventBusTests : IDisposable
{
    private readonly Mock<IDbContextFactory> _mockDbContextFactory = new();
    private readonly Mock<IDomainEventDispatcher> _mockDispatcher = new();
    private readonly OutboxSettings _outboxSettings = new() { DataSource = DataSource.SQLServer };
    private readonly TestNonOutboxContext _testContext;
    private readonly InProcessEventBus _sut;

    public InProcessEventBusTests()
    {
        _testContext = TestNonOutboxContext.Create();
        _mockDbContextFactory
            .Setup(x => x.GetDbContext(It.IsAny<DataSource>()))
            .Returns(_testContext);

        IOptions<OutboxSettings> options = Options.Create(_outboxSettings);
        _sut = new InProcessEventBus(_mockDbContextFactory.Object, _mockDispatcher.Object, options);
    }

    public void Dispose() => _testContext.Dispose();

    // ── Null guard — single event ──
    [Fact]
    public async Task PublishAsync_NullEvent_ThrowsArgumentNullException()
    {
        Func<Task> act = () => _sut.PublishAsync((IIntegrationEvent)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvent");
    }

    // ── Non-outbox path dispatches directly ──
    [Fact]
    public async Task PublishAsync_WithoutOutboxSupport_DispatchesDirectly()
    {
        var integrationEvent = new Mock<IIntegrationEvent>();

        await _sut.PublishAsync(integrationEvent.Object, CancellationToken.None);

        _mockDispatcher.Verify(
            x => x.DispatchAsync(
                It.Is<IEnumerable<IDomainEvent>>(events => events.Contains(integrationEvent.Object)),
                CancellationToken.None),
            Times.Once);
    }

    // ── Null guard — batch overload ──
    [Fact]
    public async Task PublishBatch_NullEvents_ThrowsArgumentNullException()
    {
        Func<Task> act = () => _sut.PublishAsync((IEnumerable<IIntegrationEvent>)null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("integrationEvents");
    }

    // ── Batch dispatches each event ──
    [Fact]
    public async Task PublishBatch_DispatchesEachEvent()
    {
        var event1 = new Mock<IIntegrationEvent>();
        var event2 = new Mock<IIntegrationEvent>();
        IIntegrationEvent[] events = [event1.Object, event2.Object];

        await _sut.PublishAsync(events, CancellationToken.None);

        _mockDispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), CancellationToken.None),
            Times.Exactly(2));
    }

    // ── Empty batch does not dispatch ──
    [Fact]
    public async Task PublishBatch_EmptyCollection_DoesNotDispatch()
    {
        IIntegrationEvent[] events = [];

        await _sut.PublishAsync(events, CancellationToken.None);

        _mockDispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test helpers ──

    /// <summary>
    /// A minimal <see cref="ApplicationDbContext"/> subclass with outbox support disabled,
    /// used to test the non-outbox dispatch path of <see cref="InProcessEventBus"/>.
    /// </summary>
    private sealed class TestNonOutboxContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => false;

        private TestNonOutboxContext(DbContextOptions<TestNonOutboxContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider())
        {
        }

        public static TestNonOutboxContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton<AuditSaveChangesInterceptor>(_ =>
                new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton<DomainEventSaveChangesInterceptor>(_ =>
            {
                var dispatcher = new Mock<IDomainEventDispatcher>();
                var logger = new Mock<Microsoft.Extensions.Logging.ILogger<DomainEventSaveChangesInterceptor>>();
                return new DomainEventSaveChangesInterceptor(dispatcher.Object, logger.Object);
            });
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<TestNonOutboxContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new TestNonOutboxContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
