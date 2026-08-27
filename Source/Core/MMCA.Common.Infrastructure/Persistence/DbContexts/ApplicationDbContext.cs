using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Infrastructure.Persistence.Conventions;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Scheduling;
using MMCA.Common.Infrastructure.Settings;
using StackExchange.Profiling;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Base DbContext shared by all data-source-specific contexts (SQL Server, Cosmos, SQLite).
/// Cross-cutting concerns (audit stamping, domain event capture/dispatch) are handled by
/// <see cref="AuditSaveChangesInterceptor"/> and <see cref="DomainEventSaveChangesInterceptor"/>.
/// This class provides the global named query filters (<c>SoftDelete</c> and <c>Tenant</c>) and
/// model configuration.
/// <para>
/// One instance exists per <b>physical data source</b> (database): the same context class is
/// instantiated multiple times with different <see cref="PhysicalDataSource"/> values, each
/// building a model containing only its own database's entities
/// (see <see cref="DataSourceModelCacheKeyFactory"/>).
/// </para>
/// </summary>
/// <param name="options">EF Core options forwarded to <see cref="DbContext"/>.</param>
/// <param name="serviceProvider">Service provider used when applying entity configurations and resolving interceptors.</param>
/// <param name="assemblyProvider">Provides module assemblies containing entity configurations.</param>
/// <param name="physicalDataSource">The physical data source (database) this context instance targets.</param>
public abstract class ApplicationDbContext(
    DbContextOptions options,
    IServiceProvider serviceProvider,
    IEntityConfigurationAssemblyProvider assemblyProvider,
    PhysicalDataSource physicalDataSource)
    : DbContext(options)
{
    /// <summary>Gets the physical data source key (engine + database name) this context targets.</summary>
    public DataSourceKey DataSourceKey => physicalDataSource.Key;

    /// <summary>
    /// Whether the <c>ScheduledJobs</c> table belongs in THIS context's model. Resolved once in
    /// <see cref="OnConfiguring"/> from the root provider (the same place the interceptors are
    /// resolved) and read by <see cref="ConfigureScheduler"/>, because <c>OnModelCreating</c> must
    /// not depend on anything the model cache key does not cover.
    /// <para>
    /// Two conditions, both required. <c>Scheduler:Enabled</c> keeps the model of a host that never
    /// opted in byte-identical to what it was before the scheduler shipped, so its migrations never
    /// see the table. The <c>Default</c>-source condition keeps the table in exactly one database:
    /// jobs are host-scoped, unlike the outbox, which exists once per physical source.
    /// </para>
    /// </summary>
    private bool _schedulerTableEnabled;

    /// <summary>
    /// Whether the <c>AuditTrailEntries</c> table belongs in THIS context's model. Resolved once in
    /// <see cref="OnConfiguring"/> from the root provider and read by <see cref="ConfigureAuditTrail"/>,
    /// for the same reason as <see cref="_schedulerTableEnabled"/>.
    /// <para>
    /// One condition only, unlike the scheduler: <c>AuditTrail:Enabled</c>. The trail table belongs
    /// in EVERY relational source, because a trail row must commit in the same transaction as the
    /// change it describes, and a transaction does not span databases (the outbox precedent, not the
    /// host-scoped job table).
    /// </para>
    /// </summary>
    private bool _auditTrailTableEnabled;

    /// <summary>Gets the resolved connection information for this context's physical data source.</summary>
    internal PhysicalDataSource PhysicalSource => physicalDataSource;

    /// <summary>
    /// Live accessor for the tenant the owning scope runs as, assigned by the scoped
    /// <see cref="Factory.DbContextFactory"/> at context creation. Left <see langword="null"/> for a
    /// context created outside a scope (design time, a directly-constructed test context), which
    /// reads as "no tenant".
    /// </summary>
    /// <remarks>
    /// An accessor rather than a copied value on purpose. A context can be created before the
    /// request's tenant is resolved (a middleware ordering detail no persistence code should depend
    /// on), and a copy taken at that moment would pin the context to the wrong answer for its whole
    /// life. Reading through the delegate at query and save time removes the ordering hazard
    /// entirely.
    /// </remarks>
    internal Func<string?>? TenantIdAccessor { get; set; }

    /// <summary>
    /// Gets the tenant this context is currently scoped to, or <see langword="null"/> when no
    /// tenant is resolved (background services, seeders, admin flows), in which case the
    /// <c>Tenant</c> query filter is inert and every tenant's rows are visible.
    /// </summary>
    public string? CurrentTenantId => TenantIdAccessor?.Invoke();

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
        try
        {
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Reset so a later plain base.SaveChangesAsync on this instance (e.g. an internal
            // outbox write) cannot silently reuse the previous caller's identity for its stamps.
            CurrentSaveUserId = null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden purely to run change detection once per save. See <see cref="DetectChangesOnce"/>.
    /// </remarks>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        using var detection = DetectChangesOnce();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="SaveChangesAsync(UserIdentifierType?, CancellationToken)"/>:
    /// stamps audit fields with <paramref name="userId"/> through the same interceptor pipeline.
    /// The sync path cannot dispatch events in-process; captured events are delivered by the
    /// outbox processor instead (see <see cref="DomainEventSaveChangesInterceptor"/>).
    /// </summary>
    /// <param name="userId">The current user's ID for audit stamps, or <see langword="null"/> for system operations.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public int SaveChanges(UserIdentifierType? userId)
    {
        CurrentSaveUserId = userId;
        try
        {
            return base.SaveChanges();
        }
        finally
        {
            CurrentSaveUserId = null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden purely to run change detection once per save. See <see cref="DetectChangesOnce"/>.
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        using var detection = DetectChangesOnce();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Runs change detection once, then suppresses automatic detection for the rest of the save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ChangeTracker.Entries&lt;T&gt;()</c> triggers a full <c>DetectChanges</c> on every call and
    /// memoizes nothing. Two interceptors scan the tracker from <c>SavingChanges</c>
    /// (<see cref="Interceptors.AuditSaveChangesInterceptor"/> over <c>IAuditableEntity</c>, and
    /// <see cref="Interceptors.DomainEventSaveChangesInterceptor"/> over <c>IAggregateRoot</c>), and
    /// EF then detects again on its own before building the save. A save therefore paid three
    /// O(tracked entities x properties) snapshot comparisons where one suffices.
    /// </para>
    /// <para>
    /// Detecting up front and suppressing the rest is safe because everything the interceptors do
    /// afterwards bypasses detection anyway: the audit interceptor writes through
    /// <c>entry.Property(...).CurrentValue</c>, and the domain-event interceptor adds outbox rows
    /// through <c>Add</c>. Both take effect immediately on the entry. The previous setting is
    /// restored on the way out, so a caller that had already disabled auto-detect keeps its choice
    /// and never gets an unexpected detection pass.
    /// </para>
    /// </remarks>
    private DetectChangesScope DetectChangesOnce()
    {
        var previous = ChangeTracker.AutoDetectChangesEnabled;
        if (previous)
            ChangeTracker.DetectChanges();

        ChangeTracker.AutoDetectChangesEnabled = false;
        return new DetectChangesScope(ChangeTracker, previous);
    }

    private readonly struct DetectChangesScope(ChangeTracker changeTracker, bool previousSetting) : IDisposable
    {
        public void Dispose() => changeTracker.AutoDetectChangesEnabled = previousSetting;
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

        // Registration order IS execution order, and the tenant interceptor belongs BETWEEN the two:
        // after the audit stamps (it must not run against half-stamped entries) and before the
        // domain-event interceptor serializes outbox rows, so those rows describe an entity whose
        // tenant is already final. GetService, not GetRequiredService: a directly-constructed test
        // or design-time context that never registered it must still build.
        if (serviceProvider.GetService<TenantSaveChangesInterceptor>() is { } tenantInterceptor)
        {
            optionsBuilder.AddInterceptors(auditInterceptor, tenantInterceptor, domainEventInterceptor);
        }
        else
        {
            optionsBuilder.AddInterceptors(auditInterceptor, domainEventInterceptor);
        }

        // Registration order IS execution order, and the change trail must run last: it diffs the
        // final values, after the audit stamps are written and after the outbox rows are added.
        // GetService, not GetRequiredService: a host that never calls AddAuditTrail registers no
        // interceptor, and that absence must read as "the trail is off" rather than fail every
        // context construction in the application.
        if (serviceProvider.GetService<AuditTrail.AuditTrailSaveChangesInterceptor>() is { } auditTrailInterceptor)
        {
            optionsBuilder.AddInterceptors(auditTrailInterceptor);
        }

        // Settings-gated table (see the field's remarks). GetService, not GetRequiredService: a host
        // that never calls AddScheduledJobs registers no SchedulerSettings, and that absence must
        // read as "disabled" rather than fail every context construction in the application.
        _schedulerTableEnabled =
            serviceProvider.GetService<IOptions<SchedulerSettings>>()?.Value.Enabled == true
            && string.Equals(physicalDataSource.Key.Name, DataSourceKey.DefaultName, StringComparison.Ordinal);

        // Same gate treatment, no source condition: see the field's remarks.
        _auditTrailTableEnabled = serviceProvider.GetService<IOptions<AuditTrailSettings>>()?.Value.Enabled == true;

        // Key EF's model cache by (context type, physical source name): the same context class is
        // instantiated once per database, each with a different model. Without this, the first
        // built model would silently be reused for every database.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, DataSourceModelCacheKeyFactory>();

        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);

        // Degrade relationships that cross physical data sources (runs at model finalization,
        // after EF's relationship-discovery conventions). A structural no-op when every entity
        // collapses onto one database (the monolith case).
        var registry = serviceProvider.GetRequiredService<IEntityDataSourceRegistry>();
        configurationBuilder.Conventions.Add(_ => new CrossDataSourceDegradeConvention(DataSourceKey, registry));

        // Unique indexes on soft-deletable entities exclude deleted rows (runs at finalization,
        // after module configurations have declared their indexes), so a soft-deleted row does
        // not block re-creating the "same" record. Hand-authored index filters are respected.
        configurationBuilder.Conventions.Add(_ => new SoftDeleteUniqueIndexConvention(DataSourceKey.Engine));
    }

    public override DbSet<TEntity> Set<TEntity>()
        where TEntity : class
        => base.Set<TEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplySoftDeleteFilters(modelBuilder);
        ApplyTenantFilters(modelBuilder);
        ConfigureConcurrencyTokens(modelBuilder);

        // Register keyless ValReturn<T> types mapped to no table/view — used for raw SQL scalar queries.
        modelBuilder.Entity<ValReturn<bool>>().HasNoKey().ToView(null);
        modelBuilder.Entity<ValReturn<int>>().HasNoKey().ToView(null);
        modelBuilder.Entity<ValReturn<DateTime>>().HasNoKey().ToView(null);
        modelBuilder.Entity<ValReturn<string>>().HasNoKey().ToView(null);

        // Configure the outbox table for transactional domain event persistence.
        ConfigureOutbox(modelBuilder);

        // Configure the inbox table for consumer-side idempotency (used when MessageBus:EnableInbox).
        ConfigureInbox(modelBuilder);

        // Configure the recurring job table (used when Scheduler:Enabled, Default source only).
        ConfigureScheduler(modelBuilder);

        // Configure the entity change-history table (used when AuditTrail:Enabled, every source).
        ConfigureAuditTrail(modelBuilder);
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
            modelBuilder.Entity(clrType).HasQueryFilter(SoftDeleteFilterName, filter);
        }
    }

    /// <summary>
    /// The name of the soft-delete query filter. Named so that a caller asking to include
    /// soft-deleted rows (<c>ignoreQueryFilters: true</c>) drops exactly this filter and leaves the
    /// tenant filter in force.
    /// </summary>
    internal const string SoftDeleteFilterName = "SoftDelete";

    /// <summary>The name of the tenant query filter, used to keep it out of soft-delete bypasses.</summary>
    internal const string TenantFilterName = "Tenant";

    /// <summary>The shadow-safe name of the tenant discriminator column on every tenant-owned entity.</summary>
    internal const string TenantIdPropertyName = nameof(ITenantEntity.TenantId);

    /// <summary>Column width of <c>TenantId</c>, matching the claim/header values it is fed from.</summary>
    internal const int TenantIdMaxLength = 64;

    /// <summary>
    /// Configures the tenant column and applies the named <c>Tenant</c> global query filter to every
    /// non-owned <see cref="ITenantEntity"/>. Owned types are excluded: they inherit their parent's
    /// filter, exactly as they do for soft delete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two named filters, composed by EF.</b> This one sits beside <c>SoftDelete</c>; EF combines
    /// named filters with AND, so a tenant-owned soft-deletable entity is filtered on both without
    /// either filter knowing about the other, and <c>IgnoreQueryFilters(["SoftDelete"])</c> can drop
    /// one without dropping the other.
    /// </para>
    /// <para>
    /// <b>One model serves every tenant.</b> The filter body embeds this context instance as a
    /// constant typed as the context; EF rewrites context-typed constants to the executing context
    /// at query compile time and lifts <see cref="CurrentTenantId"/> into a SQL parameter. The model
    /// cache is keyed by (context type, physical source), so two scopes on two tenants share one
    /// compiled model and still read disjoint rows.
    /// </para>
    /// <para>
    /// <b>A null tenant sees everything.</b> The <c>CurrentTenantId == null</c> disjunct is what
    /// keeps the outbox processor, the seeders and the retention jobs working: they run with no
    /// tenant and must see every tenant's rows.
    /// </para>
    /// </remarks>
    /// <param name="modelBuilder">The model builder to apply the filter to.</param>
    protected void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The tenant value is read off THIS context at query time. Typed as ApplicationDbContext so
        // EF's context-constant rewrite matches (it tests assignability from the executing context's
        // runtime type, which is always a subclass of this one).
        var contextConstant = Expression.Constant(this, typeof(ApplicationDbContext));
        var currentTenant = Expression.Property(contextConstant, nameof(CurrentTenantId));
        var nullTenant = Expression.Constant(null, typeof(string));

        foreach (var clrType in modelBuilder.Model.GetEntityTypes()
            .Where(et => typeof(ITenantEntity).IsAssignableFrom(et.ClrType) && !et.IsOwned())
            .Select(et => et.ClrType))
        {
            var entity = modelBuilder.Entity(clrType);

            entity.Property(TenantIdPropertyName)
                .IsRequired()
                .HasMaxLength(TenantIdMaxLength)
                .IsUnicode(false);

            // Every tenant-scoped read carries "TenantId = @tenant" as its leading predicate, so the
            // column that answers it must be indexed or shared-schema tenancy degrades into a scan
            // per query.
            if (physicalDataSource.Key.Engine != DataSource.CosmosDB)
            {
                entity.HasIndex(TenantIdPropertyName);
            }

            var parameter = Expression.Parameter(clrType, "e");

            // EF.Property rather than a CLR member access: it works for an explicitly implemented
            // interface member and for a shadow property, and translates identically otherwise.
            var tenantOfEntity = Expression.Call(
                typeof(EF),
                nameof(EF.Property),
                [typeof(string)],
                parameter,
                Expression.Constant(TenantIdPropertyName));

            var filter = Expression.Lambda(
                Expression.OrElse(
                    Expression.Equal(currentTenant, nullTenant),
                    Expression.Equal(tenantOfEntity, currentTenant)),
                parameter);

            entity.HasQueryFilter(TenantFilterName, filter);
        }
    }

    /// <summary>
    /// Configures the <c>RowVersion</c> property as an optimistic concurrency token on every
    /// non-owned entity type that inherits from <see cref="AuditableBaseEntity{TId}"/>.
    /// SQL Server maps this to <c>rowversion</c> (auto-incremented by the database, so the
    /// property is database-generated); other relational providers (SQLite) have no equivalent
    /// server-generated type — the property is mapped as a plain application-managed concurrency
    /// token there, so EF includes the entity's value in INSERTs instead of expecting the
    /// database to generate one.
    /// EF Core automatically includes the token in UPDATE/DELETE WHERE clauses and throws
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> on conflicts.
    /// </summary>
    protected void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var isSqlServer = Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(et => typeof(IAuditableEntity).IsAssignableFrom(et.ClrType) && !et.IsOwned()))
        {
            var property = modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(AuditableBaseEntity<>.RowVersion));

            if (isSqlServer)
            {
                property.IsRowVersion();
            }
            else
            {
                property.IsConcurrencyToken();
            }
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
            entity.Property(e => e.OrderingKey).HasMaxLength(200).IsUnicode(false);
            // Poll path (OutboxProcessor): pending rows, oldest first. The processor also filters on
            // RetryCount and LockedUntil, so both ride along as included columns; without them every
            // candidate row the index returns costs a key lookup back into the table.
            entity.HasIndex(e => new { e.ProcessedOn, e.OccurredOn })
                  .IncludeProperties(e => new { e.RetryCount, e.LockedUntil })
                  .HasFilter("[ProcessedOn] IS NULL")
                  .HasDatabaseName("IX_OutboxMessages_Pending");

            // Retention path (OutboxCleanupService): processed rows older than the cutoff. The pending
            // index above deliberately excludes exactly these rows, so without this one the six-hourly
            // sweep scans the largest partition of the table.
            entity.HasIndex(e => e.ProcessedOn)
                  .HasFilter("[ProcessedOn] IS NOT NULL")
                  .HasDatabaseName("IX_OutboxMessages_Processed");

            // Ordering path (OutboxProcessor's claim predicate): for every keyed row the claim asks
            // whether an EARLIER unprocessed row shares its key, once per candidate row. Without an
            // index seekable by (OrderingKey, OccurredOn) that question is a scan of the pending
            // partition per row. Filtered to keyed pending rows only, so hosts that never declare an
            // ordering key carry an empty index.
            entity.HasIndex(e => new { e.OrderingKey, e.OccurredOn })
                  .IncludeProperties(e => new { e.RetryCount })
                  .HasFilter("[OrderingKey] IS NOT NULL AND [ProcessedOn] IS NULL")
                  .HasDatabaseName("IX_OutboxMessages_Ordering");
        });

    /// <summary>
    /// Configures the <see cref="InboxMessage"/> entity (consumer-side idempotency). Called from
    /// <see cref="OnModelCreating"/> so all relational providers include the inbox table; Cosmos
    /// overrides <see cref="OnModelCreating"/> and skips this configuration.
    /// </summary>
    private static void ConfigureInbox(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(500).IsUnicode(false);
            entity.HasIndex(e => e.MessageId)
                  .IsUnique()
                  .HasDatabaseName("IX_InboxMessages_MessageId");

            // Retention path (OutboxCleanupService.PurgeInbox): the unique index above is on MessageId,
            // so the age-based purge had nothing to seek and scanned the table.
            entity.HasIndex(e => e.ProcessedOn)
                  .HasDatabaseName("IX_InboxMessages_ProcessedOn");
        });

    /// <summary>
    /// Configures the <see cref="ScheduledJobEntry"/> entity (the recurring job store). Unlike the
    /// outbox and inbox tables this one is <b>gated</b>: it is mapped only when the host opted in
    /// via <c>Scheduler:Enabled</c> AND this context targets the <c>Default</c> data source. A host
    /// that did not opt in gets exactly the model it had before the scheduler shipped, so the table
    /// never appears in its migrations. Cosmos DB never reaches this method (it overrides
    /// <see cref="OnModelCreating"/>).
    /// </summary>
    private void ConfigureScheduler(ModelBuilder modelBuilder)
    {
        if (!_schedulerTableEnabled)
        {
            return;
        }

        modelBuilder.Entity<ScheduledJobEntry>(entity =>
        {
            entity.ToTable("ScheduledJobs", "dbo");
            entity.HasKey(e => e.JobName);
            entity.Property(e => e.JobName).HasMaxLength(128).IsUnicode(false);
            entity.Property(e => e.CronExpression).IsRequired().HasMaxLength(128).IsUnicode(false);
            entity.Property(e => e.LastOutcome).HasMaxLength(32).IsUnicode(false);
            entity.Property(e => e.LastError).HasMaxLength(2048);

            // Claim path (ScheduledJobRunner): due rows, earliest first. Deliberately NOT filtered to
            // unlocked rows: the poll must also find rows whose lease has EXPIRED (that is how a dead
            // replica's work is reclaimed), and "LockedUntil IS NULL" would hide exactly those. The
            // lease columns ride along as included columns instead, so the predicate is answered from
            // the index without a key lookup per candidate row.
            entity.HasIndex(e => e.NextRunOn)
                  .IncludeProperties(e => new { e.LockedUntil, e.LockToken })
                  .HasDatabaseName("IX_ScheduledJobs_NextRunOn");
        });
    }

    /// <summary>
    /// Configures the <see cref="AuditTrail.AuditTrailEntry"/> entity (the change-history table).
    /// Gated on <c>AuditTrail:Enabled</c> like the scheduler table, but mapped into <b>every</b>
    /// relational source rather than one: a trail row commits in the same transaction as the change
    /// it describes, and a transaction does not span databases. Cosmos DB never reaches this method
    /// (it overrides <see cref="OnModelCreating"/>).
    /// </summary>
    private void ConfigureAuditTrail(ModelBuilder modelBuilder)
    {
        if (!_auditTrailTableEnabled)
        {
            return;
        }

        modelBuilder.Entity<AuditTrail.AuditTrailEntry>(entity =>
        {
            entity.ToTable("AuditTrailEntries", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(256).IsUnicode(false);
            entity.Property(e => e.EntityKey).IsRequired().HasMaxLength(128);
            entity.Property(e => e.PropertyName).HasMaxLength(128).IsUnicode(false);
            entity.Property(e => e.Operation).IsRequired().HasMaxLength(16).IsUnicode(false);
            entity.Property(e => e.CorrelationId).HasMaxLength(64).IsUnicode(false);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsUnicode(false);

            // Read path: one entity's history, newest first. Every shipped query and every consumer
            // screen asks the same question, so the index carries the whole predicate plus the sort.
            entity.HasIndex(e => new { e.EntityType, e.EntityKey, e.ChangedOn })
                  .HasDatabaseName("IX_AuditTrailEntries_Entity");

            // Retention path (AuditTrailCleanupJob): rows older than the cutoff, across all types.
            // The read index above leads with EntityType, so without this the nightly sweep scans
            // the largest table the framework writes (the outbox precedent).
            entity.HasIndex(e => e.ChangedOn)
                  .HasDatabaseName("IX_AuditTrailEntries_ChangedOn");
        });
    }

    /// <summary>
    /// Discovers and applies the EF entity configurations for the given engine from module
    /// Infrastructure assemblies, filtered to entities whose physical data source matches this
    /// context instance (so each database's model contains only its own entities).
    /// </summary>
    /// <param name="dataSource">The target engine whose configuration interface to match.</param>
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

        // The registry derives keys from the same attributes/namespaces as the configurations
        // themselves, so model contents and runtime routing agree by construction. Entities whose
        // configuration is not registered (e.g. one implementing the provider interface directly
        // without the attributed base classes) fall back to the engine's Default source — they are
        // included in the Default model but are not routable via the unit of work (legacy behavior).
        var registry = serviceProvider.GetRequiredService<IEntityDataSourceRegistry>();

        foreach (var assembly in assemblyProvider.GetConfigurationAssemblies())
        {
            modelBuilder.ApplyAllConfigurations(
                serviceProvider,
                assembly,
                configType,
                entityType => registry.TryGetDataSourceKey(entityType.FullName!, out var key)
                    ? key == DataSourceKey
                    : DataSourceKey.Name == DataSourceKey.DefaultName);
        }
    }
}
