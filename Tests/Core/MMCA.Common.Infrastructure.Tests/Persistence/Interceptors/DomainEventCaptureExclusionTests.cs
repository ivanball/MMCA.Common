using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Interceptors;

/// <summary>
/// Event capture is scoped-exclusion aware: <c>DbContextFactory</c> hides
/// entries from an IDENTITY_INSERT round by flipping them to Unchanged, and those rows are not
/// written that round, so their events must not be captured either. The exclusion names exact
/// instances on purpose; a state-based filter would also drop events raised on a genuinely
/// already-saved aggregate, which is how the identity module publishes registration events.
/// </summary>
public sealed class DomainEventCaptureExclusionTests : IDisposable
{
    private readonly List<IDomainEvent> _dispatched = [];
    private readonly Mock<IDomainEventDispatcher> _dispatcherMock = new();
    private readonly ExclusionTestDbContext _dbContext;

    public DomainEventCaptureExclusionTests()
    {
        _dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IEnumerable<IDomainEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IDomainEvent>, CancellationToken>((events, _) => _dispatched.AddRange(events))
            .Returns(Task.CompletedTask);

        _dbContext = ExclusionTestDbContext.Create(new DomainEventSaveChangesInterceptor(
            _dispatcherMock.Object,
            NullLogger<DomainEventSaveChangesInterceptor>.Instance,
            Mock.Of<IOutboxSignal>()));
    }

    public void Dispose() => _dbContext.Dispose();

    // ── The hidden entry's events wait for the round that writes its row ──
    [Fact]
    public async Task SaveChangesAsync_ExcludedAggregate_KeepsItsEventsAndDispatchesOnlyTheRest()
    {
        var written = Aggregate(1, "written");
        var hidden = Aggregate(2, "hidden");
        _dbContext.AddRange(written, hidden);

        DomainEventSaveChangesInterceptor.BeginCaptureExclusion(_dbContext, [hidden]);
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        finally
        {
            DomainEventSaveChangesInterceptor.EndCaptureExclusion(_dbContext);
        }

        _dispatched.Should().ContainSingle().Which.Should().BeOfType<ExclusionEvent>()
            .Which.Tag.Should().Be("written");
        hidden.DomainEvents.Should().ContainSingle(
            "an event captured for a row that is not being inserted would be published ahead of its aggregate");
        written.DomainEvents.Should().BeEmpty();
    }

    // ── ...and they are captured once the exclusion ends ──
    [Fact]
    public async Task SaveChangesAsync_AfterTheExclusionEnds_CapturesTheHeldBackEvents()
    {
        var hidden = Aggregate(2, "hidden");
        _dbContext.Add(hidden);

        DomainEventSaveChangesInterceptor.BeginCaptureExclusion(_dbContext, [hidden]);
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        finally
        {
            DomainEventSaveChangesInterceptor.EndCaptureExclusion(_dbContext);
        }

        hidden.Name = "hidden, now written";
        await _dbContext.SaveChangesAsync();

        _dispatched.Should().ContainSingle().Which.Should().BeOfType<ExclusionEvent>()
            .Which.Tag.Should().Be("hidden");
        hidden.DomainEvents.Should().BeEmpty();
    }

    // ── The exclusion is per instance, never per state ──
    [Fact]
    public async Task SaveChangesAsync_UnchangedAggregateCarryingAnEvent_IsStillCaptured()
    {
        // The authentication-service shape: an already-persisted user raises a registration event
        // on a later save. A blanket "skip Unchanged aggregates" filter would silently drop it.
        var user = Aggregate(1, "registered");
        user.ClearDomainEvents();
        _dbContext.Add(user);
        await _dbContext.SaveChangesAsync();
        _dispatched.Clear();

        _dbContext.Entry(user).State.Should().Be(EntityState.Unchanged);
        user.AddDomainEvent(new ExclusionEvent("UserRegistered"));

        await _dbContext.SaveChangesAsync();

        _dispatched.Should().ContainSingle().Which.Should().BeOfType<ExclusionEvent>()
            .Which.Tag.Should().Be("UserRegistered");
    }

    // ── An empty exclusion set clears any previous one ──
    [Fact]
    public async Task BeginCaptureExclusion_WithNoEntities_ClearsThePreviousExclusion()
    {
        var aggregate = Aggregate(1, "written");
        _dbContext.Add(aggregate);

        DomainEventSaveChangesInterceptor.BeginCaptureExclusion(_dbContext, [aggregate]);
        DomainEventSaveChangesInterceptor.BeginCaptureExclusion(_dbContext, []);
        await _dbContext.SaveChangesAsync();

        _dispatched.Should().ContainSingle();
    }

    private static ExclusionAggregate Aggregate(int id, string tag)
    {
        var aggregate = new ExclusionAggregate { Id = id, Name = tag };
        aggregate.AddDomainEvent(new ExclusionEvent(tag));
        return aggregate;
    }

    // ── Test doubles ──
    public sealed record ExclusionEvent(string Tag) : BaseDomainEvent;

    public sealed class ExclusionAggregate : AuditableAggregateRootEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ExclusionTestDbContext : ApplicationDbContext
    {
        private ExclusionTestDbContext(DbContextOptions<ExclusionTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        internal override bool SupportsOutbox => false;

        public static ExclusionTestDbContext Create(DomainEventSaveChangesInterceptor interceptor)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(interceptor);
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<ExclusionTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new ExclusionTestDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ExclusionAggregate>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
