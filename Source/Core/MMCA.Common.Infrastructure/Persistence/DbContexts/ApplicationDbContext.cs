using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using StackExchange.Profiling;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Base DbContext shared by all data-source-specific contexts (SQL Server, Cosmos, SQLite).
/// Cross-cutting concerns (audit stamping, domain event capture/dispatch) are handled by
/// <see cref="AuditSaveChangesInterceptor"/> and <see cref="DomainEventSaveChangesInterceptor"/>.
/// This class provides global soft-delete query filters and model configuration.
/// </summary>
/// <param name="options">EF Core options forwarded to <see cref="DbContext"/>.</param>
/// <param name="serviceProvider">Service provider used when applying entity configurations and resolving interceptors.</param>
/// <param name="assemblyProvider">Provides module assemblies containing entity configurations.</param>
public abstract class ApplicationDbContext(
    DbContextOptions options,
    IServiceProvider serviceProvider,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : DbContext(options)
{
    /// <summary>
    /// Keyless entity used to map scalar SQL results (e.g. from raw queries) without a backing table.
    /// </summary>
    /// <typeparam name="T">The scalar return type.</typeparam>
    internal sealed class ValReturn<T>
    {
        /// <summary>Gets or sets the scalar value returned by the query.</summary>
        public T Value { get; set; } = default!;
    }

    /// <summary>
    /// Indicates whether this context supports the transactional outbox pattern.
    /// Cosmos DB does not support relational tables, so outbox is only used with
    /// SQL Server and SQLite contexts. Read by <see cref="DomainEventSaveChangesInterceptor"/>.
    /// </summary>
    internal virtual bool SupportsOutbox => true;

    /// <summary>
    /// The current user's ID for audit stamps, set before <c>base.SaveChangesAsync</c> and
    /// read by <see cref="AuditSaveChangesInterceptor"/>. When <see langword="null"/>,
    /// the interceptor uses the default value as a sentinel for system-generated entries.
    /// </summary>
    internal UserIdentifierType? CurrentSaveUserId { get; private set; }

    /// <summary>
    /// Saves all pending changes with auditing and domain event dispatch.
    /// Sets <see cref="CurrentSaveUserId"/> so interceptors can stamp audit fields,
    /// then delegates to <c>base.SaveChangesAsync</c> which triggers the interceptor pipeline.
    /// </summary>
    /// <param name="userId">The current user's ID for audit stamps, or <see langword="null"/> for system operations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public async Task<int> SaveChangesAsync(UserIdentifierType? userId, CancellationToken cancellationToken = default)
    {
        using var step = MiniProfiler.Current?.Step("MMCA.Common.Infrastructure.ApplicationDbContext: SaveChangesAsync");
        CurrentSaveUserId = userId;
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // Register interceptors resolved from DI so that audit stamping and
        // domain event capture/dispatch happen via the EF interceptor pipeline
        // rather than inline in SaveChangesAsync.
        var auditInterceptor = serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>();
        var domainEventInterceptor = serviceProvider.GetRequiredService<DomainEventSaveChangesInterceptor>();
        optionsBuilder.AddInterceptors(auditInterceptor, domainEventInterceptor);

        base.OnConfiguring(optionsBuilder);
    }

    public override DbSet<TEntity> Set<TEntity>()
        where TEntity : class
        => base.Set<TEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplySoftDeleteFilters(modelBuilder);
        ConfigureConcurrencyTokens(modelBuilder);

        // Register keyless ValReturn<T> types mapped to no table/view — used for raw SQL scalar queries.
        modelBuilder.Entity<ValReturn<bool>>().HasNoKey().ToView(null);
        modelBuilder.Entity<ValReturn<int>>().HasNoKey().ToView(null);
        modelBuilder.Entity<ValReturn<DateTime>>().HasNoKey().ToView(null);
        modelBuilder.Entity<ValReturn<string>>().HasNoKey().ToView(null);

        // Configure the outbox table for transactional domain event persistence.
        ConfigureOutbox(modelBuilder);
    }

    /// <summary>
    /// Applies a global soft-delete query filter to every non-owned <see cref="IAuditableEntity"/>.
    /// Uses expression trees because the entity type is only known at runtime.
    /// Owned types are excluded — they inherit the filter from their parent entity.
    /// Extracted as a protected method so data-source-specific contexts (e.g. Cosmos) can
    /// apply soft-delete filters independently of the full <see cref="OnModelCreating"/> pipeline.
    /// </summary>
    protected static void ApplySoftDeleteFilters(ModelBuilder modelBuilder)
    {
        foreach (var clrType in modelBuilder.Model.GetEntityTypes()
            .Where(et => typeof(IAuditableEntity).IsAssignableFrom(et.ClrType) && !et.IsOwned())
            .Select(et => et.ClrType))
        {
            var parameter = Expression.Parameter(clrType, "e");
            var property = Expression.Property(parameter, nameof(IAuditableEntity.IsDeleted));
            var filter = Expression.Lambda(
                Expression.Equal(property, Expression.Constant(false)),
                parameter
            );
            modelBuilder.Entity(clrType).HasQueryFilter("SoftDelete", filter);
        }
    }

    /// <summary>
    /// Configures the <c>RowVersion</c> property as an optimistic concurrency token on every
    /// non-owned entity type that inherits from <see cref="AuditableBaseEntity{TId}"/>.
    /// SQL Server maps this to <c>rowversion</c> (auto-incremented by the database);
    /// SQLite maps it to a <c>BLOB</c> column (application-managed).
    /// EF Core automatically includes the token in UPDATE/DELETE WHERE clauses and throws
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> on conflicts.
    /// </summary>
    protected static void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(et => typeof(IAuditableEntity).IsAssignableFrom(et.ClrType) && !et.IsOwned()))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(AuditableBaseEntity<>.RowVersion))
                .IsRowVersion();
        }
    }

    /// <summary>
    /// Configures the <see cref="OutboxMessage"/> entity in the model. Called from
    /// <see cref="OnModelCreating"/> so all relational providers include the outbox table.
    /// Cosmos DB overrides <see cref="OnModelCreating"/> and skips this configuration.
    /// </summary>
    private static void ConfigureOutbox(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(500).IsUnicode(false);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(4000);
            entity.Property(e => e.TraceId).HasMaxLength(64).IsUnicode(false);
            entity.Property(e => e.SpanId).HasMaxLength(64).IsUnicode(false);
            entity.HasIndex(e => new { e.ProcessedOn, e.OccurredOn })
                  .HasFilter("[ProcessedOn] IS NULL")
                  .HasDatabaseName("IX_OutboxMessages_Pending");
        });

    /// <summary>
    /// Discovers and applies all EF entity configurations for the given data source from module Infrastructure assemblies.
    /// </summary>
    /// <param name="dataSource">The target data source whose configuration interface to match.</param>
    /// <param name="modelBuilder">The model builder to apply configurations to.</param>
    protected void ApplyConfigurationsForEntitiesInContext(DataSource dataSource, ModelBuilder modelBuilder)
    {
        var configType = dataSource switch
        {
            DataSource.CosmosDB => typeof(IEntityTypeConfigurationCosmos<,>),
            DataSource.Sqlite => typeof(IEntityTypeConfigurationSqlite<,>),
            DataSource.SQLServer => typeof(IEntityTypeConfigurationSQLServer<,>),
            _ => throw new InvalidOperationException($"DataSource \"{dataSource}\" not implemented."),
        };

        foreach (var assembly in assemblyProvider.GetConfigurationAssemblies())
        {
            modelBuilder.ApplyAllConfigurations(serviceProvider, assembly, configType);
        }
    }
}
