using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <c>EFRawSqlQueryExecutor</c>, the Application layer's parameterized raw-SQL surface,
/// against a real SQLite database: a DTO shape, a scalar shape, the parameterization the
/// <see cref="FormattableString"/>-only signature exists to guarantee, and the refusal a Cosmos host
/// gets instead of a statement it cannot run.
/// </summary>
public sealed class EFRawSqlQueryExecutorTests : IDisposable
{
    private readonly List<string> _commandLog = [];
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly WidgetContext _dbContext;

    public EFRawSqlQueryExecutorTests()
    {
        _connection.Open();
        _dbContext = WidgetContext.Create(_connection, _commandLog);
        _dbContext.Database.EnsureCreated();
        _dbContext.Add(new Widget { Id = 1, Name = "Alpha" });
        _dbContext.Add(new Widget { Id = 2, Name = "Beta" });
        _dbContext.SaveChanges();
        _commandLog.Clear();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private EFRawSqlQueryExecutor Executor(DataSource engine = DataSource.Sqlite) =>
        new(new SingleContextFactory(_dbContext), new FixedEngineResolver(engine));

    [Fact]
    public async Task QueryAsync_MaterializesADtoShape()
    {
        var rows = await Executor().QueryAsync<WidgetRow>(
            $"SELECT Id AS Id, Name AS Name FROM Widgets ORDER BY Id");

        rows.Select(r => r.Name).Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_MaterializesAScalar()
    {
        var name = "Alpha";

        var count = await Executor().QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) AS Value FROM Widgets WHERE Name = {name}");

        count.Should().Be(1);
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_ReturnsTheDefault_WhenNothingMatches()
    {
        var name = "Gamma";

        var row = await Executor().QuerySingleOrDefaultAsync<WidgetRow>(
            $"SELECT Id AS Id, Name AS Name FROM Widgets WHERE Name = {name}");

        row.Should().BeNull();
    }

    [Fact]
    public async Task InterpolatedValues_BecomeParameters_NotInlinedLiterals()
    {
        var name = "Alpha";

        var rows = await Executor().QueryAsync<WidgetRow>(
            $"SELECT Id AS Id, Name AS Name FROM Widgets WHERE Name = {name}");

        rows.Should().ContainSingle();

        var command = _commandLog.Should().ContainSingle(entry => entry.Contains("SELECT", StringComparison.Ordinal)).Subject;
        command.Should().Contain(
            "@p0",
            "the FormattableString hole must reach the server as a command parameter");
        command.Should().NotContain(
            "'Alpha'",
            "an inlined literal is both an injection surface and a new statement text per value, which "
                + "costs the server its cached plan");
    }

    [Fact]
    public async Task CosmosHost_IsRefused_WithAReasonRatherThanAFailedStatement()
    {
        var act = async () => await Executor(DataSource.CosmosDB).QueryAsync<WidgetRow>($"SELECT 1 AS Value");

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*Cosmos*");
    }

    // -- Test doubles --
    public sealed class Widget : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Unmapped row shape: EF materializes it by matching column names to properties.</summary>
    public sealed class WidgetRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Hands the executor the one context this test owns, whatever key it asks for.</summary>
    private sealed class SingleContextFactory(ApplicationDbContext context) : IDbContextFactory
    {
        public ApplicationDbContext GetDbContext(DataSourceKey dataSourceKey) => context;

        public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public int SaveChanges() => 0;

        public void RequestIdentityInsert()
        {
            // No identity-insert path in these tests.
        }

        public void BeginTransaction()
        {
            // No transaction path in these tests.
        }

        public void CommitTransaction()
        {
            // No transaction path in these tests.
        }

        public void RollbackTransaction()
        {
            // No transaction path in these tests.
        }

        public Task<TResult> ExecuteInTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default) =>
            operation is null ? Task.FromResult<TResult>(default!) : operation(cancellationToken);

        public Task MigrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public void Dispose()
        {
            // The test owns the context's lifetime.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Answers every logical name with the default source of one fixed engine.</summary>
    private sealed class FixedEngineResolver(DataSource fixedEngine) : IDataSourceResolver
    {
        public DataSourceKey ResolveLogical(DataSource engine, string logicalName) =>
            DataSourceKey.Default(fixedEngine);

        public PhysicalDataSource GetPhysical(DataSourceKey key) => new(key, string.Empty, null, string.Empty);
    }

    public sealed class WidgetContext : ApplicationDbContext
    {
        internal override bool SupportsOutbox => true;

        private WidgetContext(DbContextOptions<WidgetContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NoModuleAssemblies(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static WidgetContext Create(SqliteConnection connection, ICollection<string> commandLog)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider serviceProvider = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<WidgetContext>()
                .UseSqlite(connection)
                .LogTo(commandLog.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
                .Options;

            return new WidgetContext(options, serviceProvider);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Widget>(e =>
            {
                e.ToTable("Widgets");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NoModuleAssemblies : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
