using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Runs the shipped delete-behavior fitness functions against the framework's OWN finalized models,
/// on both relational contexts a consumer can host (<c>SqliteDbContext</c> and
/// <c>SQLServerDbContext</c>): nothing MMCA.Common maps may cascade without an explicit
/// <c>OnDelete(DeleteBehavior.Cascade)</c>, and everything the convention touched restricts.
/// <para>
/// The framework's own tables (outbox, inbox, internal commands, scheduler, audit trail, refresh
/// sessions, permission grants, notifications) declare no relationships between each other, so today
/// both rules pass over a model whose only foreign keys are ownership ones. That is the point of the
/// gate: the day a framework table gains a relationship, this test decides whether its delete
/// behavior was a decision or an accident, before the consumers inherit it in a migration.
/// </para>
/// </summary>
public sealed class DeleteBehaviorConventionTests : DeleteBehaviorConventionTestsBase
{
    /// <inheritdoc />
    protected override object Model { get; } = FrameworkModels.Sqlite();

    [Fact]
    public void SqlServerModel_CascadingDeletes_AreExplicitlyOptedIn()
    {
        var model = FrameworkModels.SqlServer();
        var act = () => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void SqlServerModel_ConventionDeletes_AreRestricted()
    {
        var model = FrameworkModels.SqlServer();
        var act = () => ArchitectureRules.ConventionForeignKeysRestrictDeletes(model);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Builds the framework's finalized models without a database. Only the model is asserted, so
    /// neither context ever opens its connection: EF builds the model from the configurations alone.
    /// </summary>
    private static class FrameworkModels
    {
        public static object Sqlite()
        {
            var options = new DbContextOptionsBuilder<SqliteDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            using var context = new SqliteDbContext(
                options,
                Services(),
                new NoModuleAssemblies(),
                new PhysicalDataSource(DataSourceKey.Default(DataSource.Sqlite), "DataSource=:memory:", null, "Test"));

            return context.Model;
        }

        public static object SqlServer()
        {
            var options = new DbContextOptionsBuilder<SQLServerDbContext>()
                .UseSqlServer("Server=unused;Database=unused")
                .Options;

            using var context = new SQLServerDbContext(
                options,
                Services(),
                new NoModuleAssemblies(),
                new PhysicalDataSource(DataSourceKey.Default(DataSource.SQLServer), "Server=unused;Database=unused", null, "Test"));

            return context.Model;
        }

        /// <summary>The minimum registrations <c>ApplicationDbContext.OnConfiguring</c> demands.</summary>
        private static ServiceProvider Services()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                new NoDomainEventDispatcher(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                new NoOutboxSignal()));
            services.AddSingleton<IEntityDataSourceRegistry>(new NoEntityDataSources());
            return services.BuildServiceProvider();
        }
    }

    /// <summary>No module assemblies: the model holds the framework's own tables only.</summary>
    private sealed class NoModuleAssemblies : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }

    /// <summary>
    /// An empty registry, which makes the cross-source degrade convention a no-op. The two lookups
    /// answer the default key rather than throwing: nothing calls them (the TryGet below answers
    /// false for every entity), and a throwing fixture would trip this repo's own domain-throw rule.
    /// </summary>
    private sealed class NoEntityDataSources : IEntityDataSourceRegistry
    {
        public DataSourceKey GetDataSourceKey(Type entityType) => DataSourceKey.Default(DataSource.SQLServer);

        public DataSourceKey GetDataSourceKey(string entityFullName) => DataSourceKey.Default(DataSource.SQLServer);

        public bool TryGetDataSourceKey(string entityFullName, out DataSourceKey key)
        {
            key = default;
            return false;
        }

        public IReadOnlyCollection<DataSourceKey> GetPhysicalSourcesInUse() => [];
    }

    /// <summary>Never invoked: no model build dispatches an event.</summary>
    private sealed class NoDomainEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Never invoked: no model build signals the outbox.</summary>
    private sealed class NoOutboxSignal : IOutboxSignal
    {
        public void Signal()
        {
            // Nothing to wake: this context is built for its model and never saves.
        }

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
