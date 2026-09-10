using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using Testcontainers.PostgreSql;

namespace MMCA.Common.Infrastructure.PostgreSQL.Tests;

/// <summary>
/// Exercises the shipped <see cref="PostgreSQLDbContext"/> against a REAL PostgreSQL server.
/// <para>
/// The unit tier asserts the EF model, which means it asserts the metadata the provider is given and
/// never the SQL the provider emits from it. That is a blind spot with teeth for this engine
/// specifically: a partial-index predicate written the SQL Server way (<c>[ProcessedOn] IS NULL</c>),
/// a soft-delete predicate comparing a boolean to <c>0</c>, and a <see cref="DateTime"/> whose
/// <see cref="DateTime.Kind"/> is not UTC all produce a perfectly valid model and are rejected by the
/// server. Every one of them fails at <c>CREATE INDEX</c> or at the first write, in production.
/// </para>
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job (<c>postgresql-integration</c>).
/// </para>
/// </summary>
public sealed class PostgreSQLPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private ServiceProvider _serviceProvider = null!;
    private DataSourceResolver _resolver = null!;
    private PhysicalDbContextFactory _physicalFactory = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var connectionStrings = new ConnectionStringSettings
        {
            PostgreSQLConnectionString = _postgres.GetConnectionString(),
        };

        _resolver = new DataSourceResolver(
            Options.Create(connectionStrings),
            new DataSourcesSettings(),
            NullLogger<DataSourceResolver>.Instance);

        var assemblyProvider = new FixedAssemblyProvider();
        var registry = new EntityDataSourceRegistry(assemblyProvider, _resolver);

        _serviceProvider = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IDomainEventDispatcher, RecordingDomainEventDispatcher>()
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton<IEntityDataSourceRegistry>(registry)
            .AddSingleton<IDataSourceResolver>(_resolver)
            .BuildServiceProvider();

        _physicalFactory = new PhysicalDbContextFactory(_serviceProvider, _resolver, assemblyProvider);

        // The whole point of the first assertion: this is where a predicate PostgreSQL cannot parse
        // stops the run, because EnsureCreated emits every CREATE TABLE and CREATE INDEX the model
        // declares, including the three partial outbox indexes and the partial unique index the
        // soft-delete convention adds.
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task EnsureCreated_CreatesTheFrameworkTablesInTheModuleSchema()
    {
        await using var context = CreateContext();

        var tables = await context.Database
            .SqlQueryRaw<string>(
                """SELECT tablename AS "Value" FROM pg_tables WHERE schemaname = 'dbo'""")
            .ToListAsync(TestContext.Current.CancellationToken);

        tables.Should().Contain("OutboxMessages", "the framework's own tables must reach a PostgreSQL schema");
        tables.Should().Contain("InboxMessages");
        tables.Should().Contain(nameof(PgThing), "the entity mapping mirrors SQL Server, module schema included");
    }

    // The three partial indexes the outbox depends on. A bracketed identifier survives EF's model
    // build and dies here, which is exactly why this assertion runs against a server.
    [Fact]
    public async Task EnsureCreated_CreatesTheOutboxPartialIndexes()
    {
        await using var context = CreateContext();

        var definitions = await context.Database
            .SqlQueryRaw<string>(
                """SELECT indexdef AS "Value" FROM pg_indexes WHERE schemaname = 'dbo' AND tablename = 'OutboxMessages' AND indexdef LIKE '%WHERE%'""")
            .ToListAsync(TestContext.Current.CancellationToken);

        definitions.Should().HaveCount(3, "the pending, processed and ordering indexes are all filtered");
        definitions.Should().AllSatisfy(d => d.Should().NotContain("[", "PostgreSQL has no bracketed identifiers"));
    }

    // Npgsql maps DateTime to timestamptz. If the framework ever hands it a value whose Kind is not
    // UTC the write throws, so the round-trip below is the assertion that the normalizing converter
    // is actually in the pipeline.
    [Fact]
    public async Task AuditStamps_RoundTripAsUtcTimestamps()
    {
        var code = NewCode();

        await using (var context = CreateContext())
        {
            context.Add(new PgThing { Id = default, Code = code });
            await context.SaveChangesAsync(42, TestContext.Current.CancellationToken);
        }

        await using var reader = CreateContext();
        var saved = await reader.Set<PgThing>().SingleAsync(t => t.Code == code, TestContext.Current.CancellationToken);

        saved.CreatedBy.Should().Be(42, "the audit interceptor stamps the current user");
        saved.CreatedOn.Kind.Should().Be(DateTimeKind.Utc, "timestamptz always reads back as UTC");
        saved.CreatedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        var columnType = await reader.Database
            .SqlQueryRaw<string>(
                """SELECT data_type AS "Value" FROM information_schema.columns WHERE table_schema = 'dbo' AND table_name = 'PgThing' AND column_name = 'CreatedOn'""")
            .SingleAsync(TestContext.Current.CancellationToken);

        columnType.Should().Be("timestamp with time zone");
    }

    [Fact]
    public async Task SoftDelete_HidesTheRowFromTheGlobalFilter()
    {
        var code = NewCode();

        await using (var context = CreateContext())
        {
            context.Add(new PgThing { Id = default, Code = code });
            await context.SaveChangesAsync(1, TestContext.Current.CancellationToken);
        }

        await using (var context = CreateContext())
        {
            var thing = await context.Set<PgThing>().SingleAsync(t => t.Code == code, TestContext.Current.CancellationToken);
            thing.Delete().IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync(1, TestContext.Current.CancellationToken);
        }

        await using var reader = CreateContext();
        (await reader.Set<PgThing>().AnyAsync(t => t.Code == code, TestContext.Current.CancellationToken))
            .Should().BeFalse("the soft-delete query filter hides it");

        var deleted = await reader.Set<PgThing>()
            .IgnoreQueryFilters()
            .SingleAsync(t => t.Code == code, TestContext.Current.CancellationToken);

        deleted.IsDeleted.Should().BeTrue();
        deleted.DeletedOn.Should().NotBeNull();
        deleted.DeletedOn!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    // The payoff of the boolean soft-delete predicate: a deleted row must stop occupying its unique
    // slot. With the SQL Server spelling ("IsDeleted" = 0) the index never gets created at all, and
    // with no predicate at all this insert fails on a duplicate key.
    [Fact]
    public async Task SoftDeletedRow_FreesItsUniqueSlot()
    {
        var code = NewCode();

        await using (var context = CreateContext())
        {
            context.Add(new PgThing { Id = default, Code = code });
            await context.SaveChangesAsync(1, TestContext.Current.CancellationToken);

            var thing = await context.Set<PgThing>().SingleAsync(t => t.Code == code, TestContext.Current.CancellationToken);
            thing.Delete();
            await context.SaveChangesAsync(1, TestContext.Current.CancellationToken);
        }

        await using var context2 = CreateContext();
        context2.Add(new PgThing { Id = default, Code = code });

        var act = async () => await context2.SaveChangesAsync(1, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("the partial unique index excludes soft-deleted rows");
    }

    // The transactional outbox on PostgreSQL: a local domain event writes a row in the same
    // transaction as the aggregate and is drained (ProcessedOn stamped) after the save.
    [Fact]
    public async Task LocalDomainEvent_WritesAnOutboxRowAndDrainsIt()
    {
        var code = NewCode();

        await using (var context = CreateContext())
        {
            var thing = new PgThing { Id = default, Code = code };
            thing.AddCreatedEvent();
            context.Add(thing);
            await context.SaveChangesAsync(1, TestContext.Current.CancellationToken);
        }

        await using var reader = CreateContext();
        var row = await reader.Set<OutboxMessage>()
            .SingleAsync(m => m.EventType.Contains(nameof(PgThingCreated)), TestContext.Current.CancellationToken);

        row.Payload.Should().Contain(code);
        row.OccurredOn.Kind.Should().Be(DateTimeKind.Utc);
        row.ProcessedOn.Should().NotBeNull("a local domain event is dispatched in process and its row marked processed");
    }

    // An integration event deliberately stays pending for the OutboxProcessor. Reading it back with
    // the processor's own predicate is what runs the query the pending partial index serves.
    [Fact]
    public async Task IntegrationEvent_LeavesAPendingOutboxRow()
    {
        var code = NewCode();

        await using (var context = CreateContext())
        {
            var thing = new PgThing { Id = default, Code = code };
            thing.AddShippedEvent();
            context.Add(thing);
            await context.SaveChangesAsync(1, TestContext.Current.CancellationToken);
        }

        await using var reader = CreateContext();
        var pending = await reader.Set<OutboxMessage>()
            .Where(m => m.ProcessedOn == null && m.RetryCount < 5)
            .OrderBy(m => m.OccurredOn)
            .ToListAsync(TestContext.Current.CancellationToken);

        pending.Should().ContainSingle(m => m.Payload.Contains(code, StringComparison.Ordinal));
    }

    private static string NewCode() => $"code-{Guid.NewGuid():N}";

    private ApplicationDbContext CreateContext() =>
        _physicalFactory.Create(_resolver.ResolveLogical(DataSource.PostgreSQL, DataSourceKey.DefaultName));

    private sealed class FixedAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() =>
            [typeof(PostgreSQLPersistenceTests).Assembly];
    }

    /// <summary>
    /// Stands in for the in-process dispatcher: the outbox routing only needs the call to succeed,
    /// and a handler of its own would test the dispatcher rather than the provider.
    /// </summary>
    private sealed class RecordingDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [IdValueGenerated]
    public sealed class PgThing : AuditableAggregateRootEntity<int>
    {
        public string Code { get; set; } = string.Empty;

        public void AddCreatedEvent() => AddDomainEvent(new PgThingCreated(Code));

        public void AddShippedEvent() => AddDomainEvent(new PgThingShipped(Code));
    }

    public sealed record PgThingCreated(string Code) : BaseDomainEvent;

    public sealed record PgThingShipped(string Code) : BaseDomainEvent, IIntegrationEvent;

    [UseDatabase("Default")]
    private sealed class PgThingConfiguration : EntityTypeConfigurationPostgreSQL<PgThing, int>
    {
        public override void Configure(EntityTypeBuilder<PgThing> builder)
        {
            base.Configure(builder);

            builder.Property(p => p.Code).IsRequired().HasMaxLength(80);

            // Unique, so SoftDeleteUniqueIndexConvention narrows it to the live rows and the server
            // has to accept the boolean predicate.
            builder.HasIndex(p => p.Code).IsUnique();
        }
    }
}
