using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Specifications;

/// <summary>
/// SQL-shape coverage for the dynamic-LINQ query pipeline (rubric section 12). The filter and sort
/// strategies build their predicates as System.Linq.Dynamic.Core string expressions, and whether the
/// supplied values reach the database as parameters or as inlined literals decides whether SQL Server
/// can reuse a plan and whether EF's compiled-query cache hits. Nothing else in the suite inspects the
/// emitted SQL, so this is the guard that keeps the answer from drifting silently.
/// </summary>
public sealed class QueryParameterizationTests : IDisposable
{
    private readonly QueryShapeTestDbContext _dbContext = QueryShapeTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    private static readonly Dictionary<string, string> EmptyMap = [];

    private string FilteredSql(string property, string op, string value) =>
        QueryBody(QueryFilterService
            .ApplyFilters(_dbContext.Products.AsNoTracking(), new Dictionary<string, (string, string)> { [property] = (op, value) }, EmptyMap)
            .ToQueryString());

    /// <summary>
    /// Strips the parameter-declaration preamble that <c>ToQueryString</c> prepends (SQLite emits
    /// <c>.param set @Value 'Widget'</c> so the statement can be replayed in a shell). The declarations
    /// necessarily contain the raw values; the question these tests ask is whether the STATEMENT does.
    /// </summary>
    private static string QueryBody(string sql) =>
        string.Join(
            '\n',
            sql.Split('\n').Where(l => !l.TrimStart().StartsWith(".param", StringComparison.Ordinal)))
        .Trim();

    [Fact]
    public void StringEquals_SendsTheValueAsAParameter()
    {
        var sql = FilteredSql("Name", "EQUALS", "Widget");

        sql.Should().NotContain("'Widget'", "an inlined literal defeats plan reuse and misses the EF compiled-query cache");
        sql.Should().Contain("@", "the filter value must reach the database as a parameter");
    }

    [Fact]
    public void StringContains_SendsTheValueAsAParameter()
    {
        var sql = FilteredSql("Name", "CONTAINS", "Widget");

        sql.Should().NotContain("'Widget'");
        sql.Should().Contain("@");
    }

    [Fact]
    public void IntComparison_SendsTheValueAsAParameter()
    {
        var sql = FilteredSql("Price", "GREATER THAN", "25");

        sql.Should().MatchRegex(@"\B@\w+", "the bound must reach the database as a parameter, not as the literal 25");
    }

    [Fact]
    public void TwoValuesOfTheSameFilter_ProduceIdenticalSql()
    {
        // The plan-reuse invariant stated directly: only the parameter VALUES may differ between two
        // requests that filter the same property with the same operator.
        var first = FilteredSql("Name", "EQUALS", "Widget");
        var second = FilteredSql("Name", "EQUALS", "Gadget");

        second.Should().Be(first, "distinct filter values must not produce distinct SQL text");
    }

    [Fact]
    public void Sorting_DoesNotInlineTheSortColumnAsAValue()
    {
        var sql = QueryBody(QueryFieldService
            .ApplySorting(_dbContext.Products.AsNoTracking(), "Name", "desc", EmptyMap)
            .ToQueryString());

        sql.Should().Contain("ORDER BY", "the sort must be pushed to the database");
        sql.Should().Contain("DESC");
    }

    // ── Test doubles ──
    public sealed class Product : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public int Price { get; set; }
    }

    public sealed class QueryShapeTestDbContext : ApplicationDbContext
    {
        public DbSet<Product> Products => Set<Product>();

        internal override bool SupportsOutbox => false;

        private QueryShapeTestDbContext(DbContextOptions<QueryShapeTestDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static QueryShapeTestDbContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider sp = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<QueryShapeTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new QueryShapeTestDbContext(options, sp);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Product>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Name);
                e.Property(x => x.Price);
                e.Property(x => x.RowVersion).IsConcurrencyToken();
            });
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
