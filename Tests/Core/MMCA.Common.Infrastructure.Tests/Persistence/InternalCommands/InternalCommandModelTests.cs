using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// The <c>InternalCommands</c> table is UNGATED, exactly like the outbox: it belongs to every
/// relational source whether or not the host drains the queue, so that flipping
/// <c>InternalCommands:Enabled</c> is never a migration and a scheduled row can always commit in the
/// same transaction as the aggregate change that asked for it. These tests build the real
/// <see cref="ApplicationDbContext"/> model and assert exactly that.
/// </summary>
public sealed class InternalCommandModelTests
{
    [Fact]
    public void Model_ContainsTheQueueTableOnTheDefaultSource()
    {
        using var context = ModelTestContext.Create(DataSourceKey.DefaultName);

        var entityType = context.Model.FindEntityType(typeof(InternalCommandMessage));
        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be("InternalCommands");
        entityType.FindPrimaryKey()!.Properties.Select(p => p.Name)
            .Should().Equal(nameof(InternalCommandMessage.Id));
    }

    [Fact]
    public void Model_ContainsTheQueueTableOnEveryRelationalSource()
    {
        using var context = ModelTestContext.Create("Conference");

        context.Model.FindEntityType(typeof(InternalCommandMessage)).Should().NotBeNull(
            "the queue is per source like the outbox, not host-scoped like the recurring job table");
    }

    [Fact]
    public void Model_MapsTheQueueTableAlongsideTheOutbox()
    {
        using var context = ModelTestContext.Create(DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(OutboxMessage)).Should().NotBeNull();
        context.Model.FindEntityType(typeof(InternalCommandMessage))!.GetSchema().Should().Be(
            context.Model.FindEntityType(typeof(OutboxMessage))!.GetSchema());
    }

    // PostgreSQL rejects [Bracketed] identifiers outright, so a partial-index filter that kept the
    // SQL Server spelling would fail at CREATE INDEX rather than at model build. The queue table
    // goes through the same QuoteColumn helper the outbox uses (ADR-113).
    [Fact]
    public void PostgreSQLModel_QuotesTheQueueIndexPredicatesForPostgreSQL()
    {
        using var context = ModelTestContext.Create("QueueModel", DataSource.PostgreSQL);

        QueueFilters(context).Should().BeEquivalentTo(
            "\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL",
            "\"ProcessedOn\" IS NOT NULL",
            "\"DeadLetteredOn\" IS NOT NULL");
    }

    [Fact]
    public void SqlServerModel_KeepsTheBracketedQueueIndexPredicates()
    {
        using var context = ModelTestContext.Create("QueueModel", DataSource.SQLServer);

        QueueFilters(context).Should().BeEquivalentTo(
            "[ProcessedOn] IS NULL AND [DeadLetteredOn] IS NULL",
            "[ProcessedOn] IS NOT NULL",
            "[DeadLetteredOn] IS NOT NULL");
    }

    [Fact]
    public void Model_CarriesThePollRetentionAndDeadLetterIndexes()
    {
        using var context = ModelTestContext.Create(DataSourceKey.DefaultName);

        var indexNames = context.Model.FindEntityType(typeof(InternalCommandMessage))!
            .GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ToList();

        indexNames.Should().Contain([
            "IX_InternalCommands_Pending",
            "IX_InternalCommands_Processed",
            "IX_InternalCommands_DeadLettered",
        ]);
    }

    [Fact]
    public void Model_DoesNotApplyTheSoftDeleteOrTenantFiltersToQueueRows()
    {
        using var context = ModelTestContext.Create(DataSourceKey.DefaultName);

        context.Model.FindEntityType(typeof(InternalCommandMessage))!.GetDeclaredQueryFilters()
            .Should().BeEmpty("framework bookkeeping rows are neither soft-deletable nor tenant-owned");
    }

    /// <summary>The partial-index predicates the queue table declares, in declaration order.</summary>
    private static List<string?> QueueFilters(ApplicationDbContext context) =>
        [.. context.Model.FindEntityType(typeof(InternalCommandMessage))!
            .GetIndexes()
            .Select(index => index.GetFilter())
            .Where(filter => filter is not null)];

    /// <summary>
    /// A context that runs the real <see cref="ApplicationDbContext.OnModelCreating"/> against a
    /// configurable source name and engine, so the assertions above are about the production mapping.
    /// </summary>
    /// <remarks>
    /// The engine comes from the <see cref="DataSourceKey"/>, which is exactly what
    /// <c>ConfigureInternalCommands</c> branches on, while the provider stays SQLite so no server is
    /// needed. Index-include annotations are provider-prefixed and ignored by other providers, so
    /// carrying a PostgreSQL one on a SQLite-built model changes nothing about what is asserted. The
    /// DDL those predicates produce against a real server is proven by the Testcontainers tier.
    /// </remarks>
    private sealed class ModelTestContext : ApplicationDbContext
    {
        private ModelTestContext(
            DbContextOptions<ModelTestContext> options,
            IServiceProvider serviceProvider,
            PhysicalDataSource physicalDataSource)
            : base(options, serviceProvider, new NullAssemblyProvider(), physicalDataSource)
        {
        }

        public static ModelTestContext Create(string sourceName, DataSource engine = DataSource.Sqlite)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

            var options = new DbContextOptionsBuilder<ModelTestContext>()
                .UseSqlite("DataSource=:memory:")
                .EnableServiceProviderCaching(false)
                .Options;

            var physical = new PhysicalDataSource(
                new DataSourceKey(engine, sourceName), "DataSource=:memory:", null, "Test");

            return new ModelTestContext(options, services.BuildServiceProvider(), physical);
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
