using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Conventions;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conventions;

/// <summary>
/// Tests for <c>RestrictDeleteByDefaultConvention</c> (registered by
/// <c>ApplicationDbContext.ConfigureConventions</c>) on the SQLite harness: a relationship nobody
/// configured restricts rather than cascading, a configured one is left exactly as configured, an
/// ownership keeps its cascade, and every foreign key carries the annotation that says which of the
/// three it was.
/// </summary>
public sealed class RestrictDeleteByDefaultConventionTests : IDisposable
{
    private readonly DeleteBehaviorTestDbContext _dbContext = DeleteBehaviorTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public void RequiredRelationship_NobodyConfigured_Restricts()
    {
        var foreignKey = ForeignKeyOf<RequiredChild>();

        foreignKey.DeleteBehavior.Should().Be(
            DeleteBehavior.Restrict,
            "EF's own default cascades a required relationship, which deletes children in the database "
                + "below the aggregate's invariants and below soft delete");
        SourceOf(foreignKey).Should().Be(RestrictDeleteByDefaultConvention.ConventionSource);
    }

    [Fact]
    public void OptionalRelationship_NobodyConfigured_Restricts()
    {
        var foreignKey = ForeignKeyOf<OptionalChild>();

        foreignKey.DeleteBehavior.Should().Be(
            DeleteBehavior.Restrict,
            "an optional relationship defaults to ClientSetNull, which silently blanks the column of "
                + "whichever children happen to be loaded and leaves the rest untouched");
        SourceOf(foreignKey).Should().Be(RestrictDeleteByDefaultConvention.ConventionSource);
    }

    [Fact]
    public void ConfiguredCascade_IsKept_AndMarkedExplicit()
    {
        var foreignKey = ForeignKeyOf<CascadingChild>();

        foreignKey.DeleteBehavior.Should().Be(
            DeleteBehavior.Cascade,
            "the convention supplies a default, it never overrides a decision");
        SourceOf(foreignKey).Should().Be(RestrictDeleteByDefaultConvention.ExplicitSource);
    }

    [Fact]
    public void OwnedType_KeepsItsCascade_AndIsMarkedOwnership()
    {
        var ownership = _dbContext.Model.FindEntityType(typeof(Parent))!
            .GetReferencingForeignKeys()
            .Single(fk => fk.IsOwnership);

        ownership.DeleteBehavior.Should().Be(
            DeleteBehavior.Cascade,
            "an owned type has no identity apart from its owner and EF requires the cascade");
        SourceOf(ownership).Should().Be(RestrictDeleteByDefaultConvention.OwnershipSource);
    }

    [Fact]
    public async Task DeletingAParentThatStillHasChildren_Fails()
    {
        _dbContext.Add(new Parent { Id = 1, Name = "P" });
        _dbContext.Add(new RequiredChild { Id = 1, ParentId = 1 });
        await _dbContext.SaveChangesAsync();

        // A second context so the child is not tracked: the restriction must be enforced by the
        // schema, not by whatever the change tracker happens to have loaded.
        await using var other = _dbContext.NewContextOnSameDatabase();
        other.Remove(await other.Set<Parent>().SingleAsync(p => p.Id == 1));
        var act = async () => await other.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "restrict-by-default means orphaning rows fails loudly instead of deleting them silently");
    }

    private IForeignKey ForeignKeyOf<TChild>() =>
        _dbContext.Model.FindEntityType(typeof(TChild))!.GetForeignKeys().Single();

    private static string? SourceOf(IForeignKey foreignKey) =>
        foreignKey.FindAnnotation(RestrictDeleteByDefaultConvention.DeleteBehaviorSourceAnnotation)?.Value as string;

    // -- Test doubles --
    public sealed class Parent : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public ParentDetail? Detail { get; set; }
    }

    /// <summary>Owned type: its foreign key is an ownership and keeps EF's required cascade.</summary>
    public sealed class ParentDetail
    {
        public string Note { get; set; } = string.Empty;
    }

    public sealed class RequiredChild : AuditableBaseEntity<int>
    {
        public int ParentId { get; set; }

        public Parent? Parent { get; set; }
    }

    public sealed class OptionalChild : AuditableBaseEntity<int>
    {
        public int? ParentId { get; set; }

        public Parent? Parent { get; set; }
    }

    public sealed class CascadingChild : AuditableBaseEntity<int>
    {
        public int ParentId { get; set; }

        public Parent? Parent { get; set; }
    }

    public sealed class DeleteBehaviorTestDbContext : ApplicationDbContext
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _services;

        internal override bool SupportsOutbox => true;

        private DeleteBehaviorTestDbContext(
            DbContextOptions<DeleteBehaviorTestDbContext> options,
            IServiceProvider serviceProvider,
            SqliteConnection connection)
            : base(options, serviceProvider, new NoModuleAssemblies(), TestPhysicalDataSources.Sqlite())
        {
            _connection = connection;
            _services = serviceProvider;
        }

        public static DeleteBehaviorTestDbContext Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                NullLogger<DomainEventSaveChangesInterceptor>.Instance,
                Mock.Of<IOutboxSignal>()));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            IServiceProvider serviceProvider = services.BuildServiceProvider();

            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var context = new DeleteBehaviorTestDbContext(Options(connection), serviceProvider, connection);
            context.Database.EnsureCreated();
            return context;
        }

        /// <summary>A second context over the SAME in-memory database, with its own change tracker.</summary>
        public DeleteBehaviorTestDbContext NewContextOnSameDatabase() =>
            new(Options(_connection), _services, _connection);

        private static DbContextOptions<DeleteBehaviorTestDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<DeleteBehaviorTestDbContext>()
                .UseSqlite(connection)
                .Options;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Parent>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.RowVersion).IsConcurrencyToken();
                e.OwnsOne(x => x.Detail);
            });

            modelBuilder.Entity<RequiredChild>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.RowVersion).IsConcurrencyToken();
                e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            });

            modelBuilder.Entity<OptionalChild>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.RowVersion).IsConcurrencyToken();
                e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            });

            modelBuilder.Entity<CascadingChild>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.RowVersion).IsConcurrencyToken();
                e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }

    private sealed class NoModuleAssemblies : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<System.Reflection.Assembly> GetConfigurationAssemblies() => [];
    }
}
