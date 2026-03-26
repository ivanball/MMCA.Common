using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using StackExchange.Profiling;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Base DbContext shared by all data-source-specific contexts (SQL Server, Cosmos, SQLite).
/// Provides auditing stamp assignment, domain event capture/dispatch, and global soft-delete query filters.
/// </summary>
/// <param name="options">EF Core options forwarded to <see cref="DbContext"/>.</param>
/// <param name="serviceProvider">Service provider used when applying entity configurations.</param>
/// <param name="timeProvider">Provides UTC timestamps for audit fields.</param>
/// <param name="logger">Logger for persistence errors.</param>
/// <param name="domainEventDispatcher">Dispatches domain events after successful persistence.</param>
/// <param name="assemblyProvider">Provides module assemblies containing entity configurations.</param>
public abstract class ApplicationDbContext(
    DbContextOptions options,
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<ApplicationDbContext> logger,
    IDomainEventDispatcher domainEventDispatcher,
    IEntityConfigurationAssemblyProvider assemblyProvider)
    : DbContext(options)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IDomainEventDispatcher _domainEventDispatcher = domainEventDispatcher ?? throw new ArgumentNullException(nameof(domainEventDispatcher));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
    /// SQL Server and SQLite contexts.
    /// </summary>
    protected virtual bool SupportsOutbox => true;

    /// <summary>
    /// Stamps audit fields, persists changes (including outbox entries), then dispatches domain events.
    /// Domain events are persisted to the outbox table in the same transaction as the aggregate
    /// changes (guaranteeing at-least-once delivery). Events are then dispatched in-process
    /// immediately for low-latency handling. The <see cref="Outbox.OutboxProcessor"/> acts as
    /// a safety net, retrying any entries that were not dispatched (e.g. due to a process crash).
    /// </summary>
    private async Task<int> SaveChangesAsyncInternal(UserIdentifierType? userId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // When no authenticated user is available (e.g. background services, seeding, outbox processing),
        // use the default value as a sentinel (0 for int, Guid.Empty for Guid). Real user IDs are
        // always non-default, so this reliably distinguishes system-generated audit entries.
        var resolvedUserId = userId ?? default;

        // Stamp audit fields automatically — prevents callers from needing to set them manually.
        // On Added: set both Created and LastModified; on Modified: only update LastModified
        // and explicitly mark Created fields as unmodified to prevent accidental overwrites.
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.CreatedOn)).CurrentValue = now;
                    entry.Property(nameof(IAuditableEntity.LastModifiedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.LastModifiedOn)).CurrentValue = now;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.CreatedOn)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.LastModifiedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.LastModifiedOn)).CurrentValue = now;
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                case EntityState.Deleted:
                default:
                    break;
            }
        }
        try
        {
            // Capture domain events BEFORE save — entities in Deleted state are detached
            // after SaveChanges, which would silently lose their events.
            var aggregateRootEntities = ChangeTracker.Entries<IAggregateRoot>()
                .Where(e => e.Entity.DomainEvents is { Count: > 0 })
                .ToArray();

            var domainEvents = aggregateRootEntities
                .SelectMany(e => e.Entity.DomainEvents)
                .ToArray();

            // Persist events to the outbox table in the same transaction as aggregate changes.
            // This guarantees at-least-once delivery even if the process crashes after save.
            var outboxEntries = PersistToOutbox(domainEvents);

            var result = await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Dispatch in-process AFTER successful persistence (optimistic, low-latency path).
            await _domainEventDispatcher.DispatchAsync(domainEvents, cancellationToken).ConfigureAwait(false);

            // Mark outbox entries as processed so the background processor skips them.
            MarkOutboxAsProcessed(outboxEntries, now);

            foreach (var aggregateRootEntity in aggregateRootEntities)
                aggregateRootEntity.Entity.ClearDomainEvents();

            return result;
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx, "Database update error: {Message}", dbEx.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in SaveChangesAsync: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Serializes domain events into <see cref="OutboxMessage"/> entries and adds them
    /// to the change tracker so they are persisted in the same transaction.
    /// </summary>
    private List<OutboxMessage> PersistToOutbox(IDomainEvent[] domainEvents)
    {
        var entries = new List<OutboxMessage>(domainEvents.Length);

        if (!SupportsOutbox || domainEvents.Length == 0)
            return entries;

        foreach (var domainEvent in domainEvents)
        {
            var entry = OutboxMessage.FromDomainEvent(domainEvent);
            entries.Add(entry);
            Set<OutboxMessage>().Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Marks outbox entries as processed after successful in-process dispatch.
    /// Uses a direct SQL update to avoid re-entering the SaveChanges pipeline.
    /// </summary>
    private void MarkOutboxAsProcessed(List<OutboxMessage> outboxEntries, DateTime processedOn)
    {
        if (outboxEntries.Count == 0)
            return;

        try
        {
            foreach (var entry in outboxEntries)
                entry.ProcessedOn = processedOn;

            // Use the base DbContext.SaveChanges (synchronous, not our custom override)
            // to persist only the ProcessedOn updates without re-triggering audit stamps
            // or event dispatch. OutboxMessage is not an IAuditableEntity so the audit
            // loop is a no-op, and it has no domain events to capture.
            base.SaveChanges();
        }
        catch (Exception ex)
        {
            // Non-fatal: the outbox processor will eventually retry these entries.
            _logger.LogWarning(ex, "Failed to mark {Count} outbox entries as processed; the outbox processor will retry", outboxEntries.Count);
        }
    }

    /// <summary>
    /// Saves all pending changes with auditing and domain event dispatch.
    /// </summary>
    /// <param name="userId">The current user's ID for audit stamps, or <see langword="null"/> for system operations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public async Task<int> SaveChangesAsync(UserIdentifierType? userId, CancellationToken cancellationToken = default)
    {
        using var step = MiniProfiler.Current?.Step("MMCA.Common.Infrastructure.ApplicationDbContext: SaveChangesAsync");
        return await SaveChangesAsyncInternal(userId, cancellationToken).ConfigureAwait(false);
    }

    public override DbSet<TEntity> Set<TEntity>()
        where TEntity : class
        => base.Set<TEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplySoftDeleteFilters(modelBuilder);

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
