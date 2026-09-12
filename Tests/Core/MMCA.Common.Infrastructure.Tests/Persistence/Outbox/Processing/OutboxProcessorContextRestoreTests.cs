using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Context;
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
/// The delivery half of the context propagation contract: each row's captured tenant, principal and
/// correlation id are restored onto the cycle's scope BEFORE the row is published, and overwritten
/// again for the next row. That is what lets <c>BrokerMessageBus</c> stamp headers describing the
/// original request rather than the background poll, and what gives the in-process path the right
/// ambient context for free.
/// </summary>
public sealed class OutboxProcessorContextRestoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RestoreTestDbContext _dbContext;
    private readonly ServiceProvider _rootProvider;
    private readonly CapturingMessageBus _messageBus = new();
    private readonly OutboxProcessor _sut;

    public OutboxProcessorContextRestoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dispatcher = Mock.Of<IDomainEventDispatcher>();
        var assemblyProvider = Mock.Of<IEntityConfigurationAssemblyProvider>(
            p => p.GetConfigurationAssemblies() == Array.Empty<Assembly>());

        var contextServices = new ServiceCollection();
        contextServices.AddSingleton(TimeProvider.System);
        contextServices.AddSingleton(dispatcher);
        contextServices.AddSingleton(assemblyProvider);
        contextServices.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
        contextServices.AddSingleton(new DomainEventSaveChangesInterceptor(
            dispatcher,
            NullLogger<DomainEventSaveChangesInterceptor>.Instance,
            Mock.Of<IOutboxSignal>()));
        contextServices.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        ServiceProvider contextProvider = contextServices.BuildServiceProvider();

        _dbContext = new RestoreTestDbContext(
            new DbContextOptionsBuilder<RestoreTestDbContext>().UseSqlite(_connection).Options,
            contextProvider,
            assemblyProvider);
        _dbContext.Database.EnsureCreated();

        var dbContextFactory = new Mock<IDbContextFactory>();
        dbContextFactory
            .Setup(f => f.GetDbContext(DataSourceKey.Default(DataSource.SQLServer)))
            .Returns(_dbContext);

        // The three ambient services registered exactly as AddInfrastructure registers them
        // (scoped), so the restore is exercised against the real implementations.
        var services = new ServiceCollection();
        services.AddSingleton(dbContextFactory.Object);
        services.AddSingleton(dispatcher);
        // Scoped, like the real bus: resolving it is what hands the capturing double the very scope
        // the processor restores each row's context onto.
        services.AddScoped<IMessageBus>(sp =>
        {
            _messageBus.Scope = sp;
            return _messageBus;
        });
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICorrelationContext, CorrelationContext>();
        services.AddScoped<ScopedUserOverride>();
        services.AddScoped<ICurrentUserService>(sp => new OverrideOnlyCurrentUserService(
            sp.GetRequiredService<ScopedUserOverride>()));
        _rootProvider = services.BuildServiceProvider();

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        _sut = new OutboxProcessor(
            _rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(new OutboxSettings { ProcessingDelaySeconds = 0 }),
            Mock.Of<IOutboxSignal>(),
            registry.Object,
            resolver.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dbContext.Dispose();
        _rootProvider.Dispose();
        _connection.Dispose();
    }

    private void Seed(params OutboxMessage[] messages)
    {
        // OccurredOn well in the past so every row is eligible on the first cycle.
        _dbContext.Set<OutboxMessage>().AddRange(messages);
        _dbContext.SaveChanges();
        _dbContext.ChangeTracker.Clear();
    }

    private static OutboxMessage Row(OutboxOrigin origin) =>
        OutboxMessage.FromDomainEvent(
            new TestIntegrationEvent { DateOccurred = DateTime.UtcNow.AddMinutes(-5) },
            origin);

    [Fact]
    public async Task DispatchMessages_RestoresTheRowsCapturedContextBeforePublishing()
    {
        Seed(Row(new OutboxOrigin(42, "Admin,Organizer", "tenant-a", "correlation-1")));

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        var observed = _messageBus.Observations.Should().ContainSingle().Subject;
        observed.TenantId.Should().Be("tenant-a");
        observed.CorrelationId.Should().Be("correlation-1");
        observed.UserId.Should().Be(42);
        observed.Roles.Should().BeEquivalentTo("Admin", "Organizer");
    }

    [Fact]
    public async Task DispatchMessages_DoesNotLeakOneRowsPrincipalIntoTheNext()
    {
        // The scope is kept for the whole batch, so the principal has to be cleared and not merely
        // overwritten: without the clear, a system-raised row would inherit the previous user.
        Seed(
            Row(new OutboxOrigin(42, "Admin", null, "correlation-1")),
            Row(new OutboxOrigin(null, null, null, "correlation-2")));

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        _messageBus.Observations.Should().HaveCount(2);
        _messageBus.Observations[0].UserId.Should().Be(42);
        _messageBus.Observations[1].UserId.Should().BeNull();
        _messageBus.Observations[1].CorrelationId.Should().Be("correlation-2");
    }

    [Fact]
    public async Task DispatchMessages_LeavesTheScopeDefaultsAlone_ForARowThatCapturedNothing()
    {
        // Every row written before these columns existed reads back as an empty origin.
        Seed(OutboxMessage.FromDomainEvent(
            new TestIntegrationEvent { DateOccurred = DateTime.UtcNow.AddMinutes(-5) }));

        await _sut.ProcessPendingMessagesAsync(CancellationToken.None);

        var observed = _messageBus.Observations.Should().ContainSingle().Subject;
        observed.TenantId.Should().BeNull();
        observed.UserId.Should().BeNull();
        observed.Roles.Should().BeEmpty();
    }

    /// <summary>An integration event, so the batch routes through <see cref="IMessageBus"/>.</summary>
    public sealed class TestIntegrationEvent : IIntegrationEvent
    {
        public DateTime DateOccurred { get; init; }

        public Guid MessageId { get; init; } = Guid.NewGuid();
    }

    /// <summary>What the scope looked like at the instant one message was published.</summary>
    private sealed record Observation(string? TenantId, string? CorrelationId, int? UserId, string[] Roles);

    /// <summary>
    /// Stands in for <c>BrokerMessageBus</c>: it reads the same scoped services the real bus reads
    /// when it stamps its headers, and records what they answered per message.
    /// </summary>
    private sealed class CapturingMessageBus : IMessageBus
    {
        /// <summary>The scope the processor resolved this bus from, assigned on first resolution.</summary>
        public IServiceProvider? Scope { get; set; }

        public List<Observation> Observations { get; } = [];

        public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            var services = Scope!;
            var user = services.GetRequiredService<ICurrentUserService>();

            Observations.Add(new Observation(
                services.GetRequiredService<ITenantContext>().TenantId,
                services.GetRequiredService<ICorrelationContext>().CorrelationId,
                user.UserId,
                [.. user.Roles]));

            return Task.CompletedTask;
        }

        public async Task PublishAsync(IEnumerable<IIntegrationEvent> integrationEvents, CancellationToken cancellationToken = default)
        {
            foreach (var integrationEvent in integrationEvents)
                await PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An <see cref="ICurrentUserService"/> that answers only from <see cref="ScopedUserOverride"/>,
    /// which is exactly what the framework's impersonating decorator reduces to outside an HTTP
    /// request.
    /// </summary>
    private sealed class OverrideOnlyCurrentUserService(ScopedUserOverride userOverride) : ICurrentUserService
    {
        public System.Security.Claims.ClaimsPrincipal User =>
            userOverride.Principal ?? new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

        public UserIdentifierType? UserId =>
            int.TryParse(
                User.FindFirst(MMCA.Common.Shared.Auth.AuthClaimTypes.Subject)?.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id)
                ? id
                : null;

        public string? Role => null;

        public T? GetClaimValue<T>(string claimType)
            where T : struct, IParsable<T> => null;
    }

    /// <summary>
    /// A SQLite-backed <see cref="ApplicationDbContext"/> mapping only the outbox table, without the
    /// SQL Server partial-index filters SQLite rejects.
    /// </summary>
    private sealed class RestoreTestDbContext(
        DbContextOptions options,
        IServiceProvider serviceProvider,
        IEntityConfigurationAssemblyProvider assemblyProvider)
        : ApplicationDbContext(options, serviceProvider, assemblyProvider, TestPhysicalDataSources.Sqlite())
    {
        internal override bool SupportsOutbox => true;

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
