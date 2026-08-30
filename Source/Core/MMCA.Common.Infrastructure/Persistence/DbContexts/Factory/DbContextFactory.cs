using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Settings;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Creates and caches <see cref="ApplicationDbContext"/> instances per physical
/// <see cref="DataSourceKey"/> (engine + database). Each physical source yields at most one
/// context per scope; subsequent calls return the cached instance.
/// Coordinates save, transaction, and disposal across all active contexts.
/// <para>
/// Under database-per-tenant it is also the routing point: a source the current tenant declares an
/// override for is created against that tenant's connection string, keeping the same
/// <see cref="DataSourceKey"/> so EF still compiles one model per source across every tenant.
/// </para>
/// </summary>
/// <param name="physicalDbContextFactory">Creates the raw contexts.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical sources in use.</param>
/// <param name="dataSourceResolver">Resolves connection information per physical source.</param>
/// <param name="currentUserService">Supplies the user id used for audit stamps on save.</param>
/// <param name="tenantContext">
/// The scope's tenant, or <see langword="null"/> in a container that never registered tenancy.
/// Read live by every context this factory creates.
/// </param>
/// <param name="tenancySettings">
/// Bound tenancy configuration, consulted only for per-tenant connection overrides. Defaulted so
/// the pre-tenancy constructor shape keeps resolving.
/// </param>
public sealed class DbContextFactory(
    IPhysicalDbContextFactory physicalDbContextFactory,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    ICurrentUserService currentUserService,
    ITenantContext? tenantContext = null,
    IOptions<TenancySettings>? tenancySettings = null
) : IDbContextFactory
{
    /// <summary>
    /// Bound on the save re-loop in <see cref="SaveChangesAsync"/>. Two passes cover the realistic
    /// case (a handler touching one further source); the third is slack before giving up rather
    /// than spinning.
    /// </summary>
    private const int MaxSavePasses = 3;

    private readonly IPhysicalDbContextFactory _physicalDbContextFactory = physicalDbContextFactory ?? throw new ArgumentNullException(nameof(physicalDbContextFactory));
    private readonly IEntityDataSourceRegistry _entityDataSourceRegistry = entityDataSourceRegistry ?? throw new ArgumentNullException(nameof(entityDataSourceRegistry));
    private readonly IDataSourceResolver _dataSourceResolver = dataSourceResolver ?? throw new ArgumentNullException(nameof(dataSourceResolver));
    private readonly ICurrentUserService _currentUserService = currentUserService ?? throw new ArgumentNullException(nameof(currentUserService));

    /// <summary>
    /// Caches one context per physical data source so all repositories within a scope share the same change tracker.
    /// </summary>
    private readonly Dictionary<DataSourceKey, ApplicationDbContext> _dbContexts = [];

    /// <summary>
    /// The tenant each per-tenant-routed context was created for. Only sources the tenant actually
    /// overrides appear here: everything else is shared and needs no guard, because the query filter
    /// and the save interceptor already scope it.
    /// </summary>
    private readonly Dictionary<DataSourceKey, string?> _routedContextTenants = [];

    /// <summary>
    /// Tracks whether a transaction is active so that contexts created lazily (after
    /// <see cref="BeginTransaction"/> was called) are automatically enlisted.
    /// </summary>
    private bool _transactionActive;

    /// <summary>
    /// When <see langword="true"/>, <see cref="SaveChangesAsync"/> scans the change tracker
    /// for Added entities with explicit identity values and handles
    /// <c>SET IDENTITY_INSERT ON/OFF</c> per table. Reset after each save.
    /// </summary>
    private bool _identityInsertRequested;

    private volatile bool _disposed;

    /// <inheritdoc />
    public ApplicationDbContext GetDbContext(DataSourceKey dataSourceKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextFactory));

        if (!_dbContexts.TryGetValue(dataSourceKey, out var context) || context is null)
        {
            var tenantOverride = ResolveTenantOverride(dataSourceKey);
            context = tenantOverride is null
                ? _physicalDbContextFactory.Create(dataSourceKey)
                : _physicalDbContextFactory.Create(dataSourceKey, tenantOverride);

            _dbContexts[dataSourceKey] = context;

            if (tenantOverride is not null)
                _routedContextTenants[dataSourceKey] = tenantContext?.TenantId;

            AttachTenantAccessor(context);

            // Enlist late-created contexts in the active transaction so that all
            // persistence within a transactional command shares the same boundary.
            if (_transactionActive && SupportsTransactions(context))
                context.Database.BeginTransaction();
        }
        else
        {
            // A routed context is bound to one tenant's database; hand it back only to that tenant.
            GuardRoutedTenantUnchanged(dataSourceKey);
        }

        return context;
    }

    /// <inheritdoc />
    public ApplicationDbContext GetDbContext(DataSource dataSource) =>
        GetDbContext(DataSourceKey.Default(dataSource));

    /// <summary>
    /// Gives a freshly created context a live view of the scope's tenant. An accessor, not a copied
    /// value: the context can be created before the request's tenant is resolved, and the query
    /// filter must read the answer that holds at query time rather than at construction time.
    /// </summary>
    /// <remarks>
    /// Written as a method taking a non-nullable parameter, and null-guarding inside, so the null
    /// tolerance a test double needs (a mocked physical factory can hand back no context at all)
    /// does not leak a maybe-null flow state back into the caller.
    /// </remarks>
    private void AttachTenantAccessor(ApplicationDbContext context)
    {
        if (context is null)
            return;

        context.TenantIdAccessor = () => tenantContext?.TenantId;
    }

    /// <summary>
    /// The tenant's own connection information for one source, or <see langword="null"/> when this
    /// source stays shared. The clone keeps the ORIGINAL <see cref="DataSourceKey"/>: the key is
    /// what EF's model cache is keyed on, so replacing only the connection string is what lets one
    /// compiled model serve every tenant's database.
    /// </summary>
    private PhysicalDataSource? ResolveTenantOverride(DataSourceKey dataSourceKey)
    {
        if (tenantContext?.TenantId is not { } tenantId
            || tenancySettings?.Value is not { } settings
            || !settings.Tenants.TryGetValue(tenantId, out var tenant)
            || !tenant.DataSources.TryGetValue(dataSourceKey.Name, out var over))
        {
            return null;
        }

        var connectionString = TenancySettingsValidator.ConnectionStringFor(dataSourceKey.Engine, over);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // The tenant overrides this source on a different engine only; this one stays shared.
            return null;
        }

        var shared = _dataSourceResolver.GetPhysical(dataSourceKey);
        return shared with
        {
            ConnectionString = connectionString,
            CosmosDatabaseName = string.IsNullOrWhiteSpace(over.CosmosDatabaseName)
                ? shared.CosmosDatabaseName
                : over.CosmosDatabaseName,
        };
    }

    /// <summary>
    /// Refuses to hand back a per-tenant-routed context after the scope's tenant changed. The
    /// cached context is bound to one physical database, so serving it to a second tenant would
    /// read and write the first tenant's data with the second tenant's filter value: the one
    /// failure mode database-per-tenant exists to make impossible.
    /// </summary>
    private void GuardRoutedTenantUnchanged(DataSourceKey dataSourceKey)
    {
        if (!_routedContextTenants.TryGetValue(dataSourceKey, out var creationTenant))
            return;

        var current = tenantContext?.TenantId;
        if (string.Equals(creationTenant, current, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            "The context for \"{0}\" was created for tenant \"{1}\" but this scope's tenant is now \"{2}\". "
            + "A per-tenant data source is bound to one database for the life of the scope; "
            + "use a fresh scope per tenant.",
            dataSourceKey,
            creationTenant ?? "<none>",
            current ?? "<none>"));
    }

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in GetSourcesInUse())
        {
            // Sources without a configured connection string are skipped (e.g. Cosmos is
            // optional in integration tests that omit its connection string).
            if (string.IsNullOrEmpty(_dataSourceResolver.GetPhysical(key).ConnectionString))
                continue;

            await GetDbContext(key).Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The physical sources this host actually uses: every source backing a registered entity,
    /// plus any already-materialized contexts (e.g. the outbox publish target).
    /// </summary>
    private List<DataSourceKey> GetSourcesInUse() =>
        [.. _entityDataSourceRegistry.GetPhysicalSourcesInUse().Union(_dbContexts.Keys)];

    /// <inheritdoc />
    /// <remarks>
    /// Iterates all cached contexts and saves each with the current user's ID for audit stamping.
    /// When <see cref="RequestIdentityInsert"/> has been called, scans the change tracker for
    /// entities with explicit identity values and splits the save into multiple rounds with
    /// <c>SET IDENTITY_INSERT ON/OFF</c> per table.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table and schema names are derived from EF model metadata, not user input.")]
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var identityInsertRequested = _identityInsertRequested;
        _identityInsertRequested = false;

        var result = 0;
        var saved = new HashSet<ApplicationDbContext>();

        // Snapshot before iterating: saving dispatches domain events in-process, and a handler that
        // resolves a repository for a source not yet materialized calls GetDbContext, which adds to
        // _dbContexts mid-enumeration. Re-loop so a context created that way is still saved; the
        // bound stops a handler that keeps creating work from looping forever.
        for (var pass = 0; pass < MaxSavePasses; pass++)
        {
            var pending = _dbContexts.Values.Where(c => !saved.Contains(c)).ToArray();
            if (pending.Length == 0)
                break;

            foreach (var context in pending)
            {
                saved.Add(context);

                result += !identityInsertRequested || context is not SQLServerDbContext
                    ? await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false)
                    : await SaveWithIdentityInsertAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }

        // Every cached context must be clean by the time the unit of work returns; anything still
        // tracked here is silently lost. The assertion reads the change tracker rather than the
        // saved set because both loss shapes must be caught: a context materialized past the pass
        // bound (never in the set) AND a handler mutating an already-saved context (in the set, yet
        // dirty). Read-only contexts and the outbox finalizer leave nothing tracked, so a clean
        // scope never trips this.
        var unsaved = _dbContexts
            .Where(entry => entry.Value.ChangeTracker.HasChanges())
            .Select(entry => entry.Key.ToString())
            .ToArray();

        if (unsaved.Length > 0)
        {
            throw new InvalidOperationException(
                "The unit of work finished with unsaved changes still tracked on: "
                + string.Join(", ", unsaved)
                + ". A domain event handler mutated or materialized a context after the bounded save loop had finished; those changes would have been discarded.");
        }

        return result;
    }

    /// <inheritdoc />
    public void RequestIdentityInsert() => _identityInsertRequested = true;

    /// <summary>
    /// Saves changes for a SQL Server context that may contain entities with explicit
    /// identity values. Groups such entities by table and saves each group separately
    /// with <c>SET IDENTITY_INSERT ON/OFF</c>, respecting SQL Server's constraint that
    /// only one table may have <c>IDENTITY_INSERT ON</c> at a time per session.
    /// </summary>
    private async Task<int> SaveWithIdentityInsertAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var identityInsertGroups = GetIdentityInsertGroups(context);

        if (identityInsertGroups.Count == 0)
            return await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);

        int result = 0;
        var allIdentityEntries = identityInsertGroups.SelectMany(g => g.Entries).ToHashSet();

        foreach (var group in identityInsertGroups)
        {
            // Temporarily hide entries from OTHER identity-insert tables so they
            // are not included in this round's batch (avoids the one-table-at-a-time constraint).
            var savedStates = allIdentityEntries.Except(group.Entries)
                .Where(e => e.State == EntityState.Added)
                .Select(e => (Entry: e, OriginalState: e.State))
                .ToList();

            foreach (var (entry, _) in savedStates)
                entry.State = EntityState.Unchanged;

            // The hidden rows are not written this round, so their domain events must not be
            // captured this round either: capture serializes an event to the outbox and clears it
            // from the aggregate, which would publish an event for a row inserted a round later.
            // The exclusion names exactly the hidden entries. A state-based filter (skip every
            // Unchanged aggregate) would also drop events legitimately raised on an already-saved
            // aggregate, which is how the identity module publishes registration events.
            DomainEventSaveChangesInterceptor.BeginCaptureExclusion(
                context,
                [.. savedStates.Select(s => s.Entry.Entity)]);

            // try/finally: a failed save must not leave IDENTITY_INSERT ON on the pooled
            // connection, nor leave the hidden entries stuck in the Unchanged state (they
            // would be silently dropped from any retried save).
            try
            {
#pragma warning disable S2077 // Schema/table identifiers come from EF model metadata (entityType.GetSchema()/GetTableName()), not user input, and SET IDENTITY_INSERT cannot take a parameterized identifier
                await context.Database.ExecuteSqlRawAsync(
                    string.Concat("SET IDENTITY_INSERT [", group.Schema, "].[", group.Table, "] ON"),
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await context.Database.ExecuteSqlRawAsync(
                        string.Concat("SET IDENTITY_INSERT [", group.Schema, "].[", group.Table, "] OFF"),
                        CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning restore S2077
            }
            finally
            {
                DomainEventSaveChangesInterceptor.EndCaptureExclusion(context);

                foreach (var (entry, originalState) in savedStates)
                    entry.State = originalState;
            }
        }

        // Final save for any remaining changes (non-identity entities, updates, etc.)
        if (context.ChangeTracker.HasChanges())
        {
            result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Scans the change tracker for Added entities with identity columns that have
    /// explicit (non-default) values, grouped by their target table.
    /// </summary>
    private static List<IdentityInsertGroup> GetIdentityInsertGroups(ApplicationDbContext context)
    {
        var groups = new Dictionary<(string Schema, string Table), List<EntityEntry>>(2);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
                continue;

            var entityType = entry.Metadata;
            var pk = entityType.FindPrimaryKey();
            if (pk is null || pk.Properties.Count != 1)
                continue;

            var idProp = pk.Properties[0];
            if (SqlServerPropertyExtensions.GetValueGenerationStrategy(idProp)
                != SqlServerValueGenerationStrategy.IdentityColumn)
            {
                continue;
            }

            // EF Core assigns temporary negative values to identity columns for entities
            // with default (0) IDs. Only entities with explicitly set (non-temporary) values
            // need IDENTITY_INSERT — those are the ones imported from an external system.
            if (entry.Property(idProp.Name).IsTemporary)
                continue;

            var schema = entityType.GetSchema() ?? "dbo";
            var table = entityType.GetTableName()!;
            var key = (schema, table);

            if (!groups.TryGetValue(key, out var entries))
            {
                entries = [];
                groups[key] = entries;
            }

            entries.Add(entry);
        }

        return [.. groups.Select(g => new IdentityInsertGroup(g.Key.Schema, g.Key.Table, g.Value))];
    }

    private sealed record IdentityInsertGroup(string Schema, string Table, List<EntityEntry> Entries);

    /// <inheritdoc />
    public int SaveChanges()
    {
        var result = 0;
        // Snapshot: see the async overload. The sync path cannot dispatch in-process, but a
        // handler is not the only thing that can materialize a context mid-loop.
        foreach (var context in _dbContexts.Values.ToArray())
            result += context.SaveChanges(_currentUserService.UserId);
        return result;
    }

    public void BeginTransaction()
    {
        _transactionActive = true;

        // Skip contexts that already carry a transaction, symmetrically with Commit/Rollback below.
        // EF throws InvalidOperationException on a second BeginTransaction for the same connection,
        // and GetDbContext already enlists late-created contexts, so a context can legitimately be
        // mid-transaction by the time this runs.
        foreach (var context in _dbContexts.Values.Where(SupportsTransactions).Where(c => !HasActiveTransaction(c)).ToArray())
            context.Database.BeginTransaction();
    }

    public void CommitTransaction()
    {
        _transactionActive = false;
        foreach (var context in _dbContexts.Values.Where(SupportsTransactions).Where(HasActiveTransaction).ToArray())
            context.Database.CommitTransaction();
    }

    public void RollbackTransaction()
    {
        _transactionActive = false;
        foreach (var context in _dbContexts.Values.Where(SupportsTransactions).Where(HasActiveTransaction).ToArray())
            context.Database.RollbackTransaction();

        // The aggregate changes and their outbox rows just rolled back; any event dispatch
        // deferred for this transaction must never run (and must not survive into an
        // execution-strategy retry of the same operation).
        foreach (var context in _dbContexts.Values.ToArray())
            DomainEventSaveChangesInterceptor.DropDeferred(context);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// With multiple physical sources, each gets its own transaction; commits are sequential and
    /// best-effort (no two-phase commit). The outbox is the cross-source consistency mechanism.
    /// </para>
    /// <para>
    /// Re-entrant: a nested call joins the ambient transaction and returns the operation's result
    /// directly. Begin, commit, rollback and the deferred-event flush belong to the outermost call
    /// alone, so the whole nest commits or rolls back as one unit.
    /// </para>
    /// <para>
    /// A returned failed <see cref="Result"/> rolls the transaction back, exactly like an
    /// exception: in a framework that mandates Result-over-exceptions (ADR-013), a handler that
    /// saves and then fails a later invariant must not leave the partial mutation committed.
    /// </para>
    /// <para>
    /// In-process domain event dispatch deferred during the transaction is flushed only after a
    /// successful commit. This also means a retrying execution strategy — which re-runs
    /// <paramref name="operation"/> wholesale on transient failures — cannot dispatch the same
    /// events once per attempt: rollback drops the aborted attempt's deferred work.
    /// </para>
    /// <para>
    /// Each retry also starts from a clean change tracker. The strategy re-runs the delegate
    /// against the same cached context instances, so entities the failed attempt added are still
    /// Added; without the reset the retry would add them a second time and insert duplicates
    /// (along with a duplicate outbox row per event). Clearing is safe because the delegate
    /// re-executes wholesale and re-reads whatever it needs.
    /// </para>
    /// <para>
    /// A failure of the <b>commit</b> itself is never retried. Its outcome is unknowable (the
    /// database may have applied the transaction and lost only the acknowledgement), and the
    /// strategy would re-run the operation against a possibly-durable commit, duplicating its
    /// writes. Such a failure surfaces as <see cref="TransactionCommitAmbiguousException"/>
    /// instead; the caller owns recovery (an <c>[Idempotent]</c> API request replays safely, and
    /// the outbox delivers whatever the commit did make durable).
    /// </para>
    /// <para>
    /// <b>Limitation, multiple physical sources:</b> commits are sequential, so a commit failure on
    /// source #2 leaves source #1 already committed. The ambiguity is then a partial commit rather
    /// than an unknown one, and the caller's replay is what reconciles it. The thrown
    /// <see cref="TransactionCommitAmbiguousException"/> names each source's outcome (committed,
    /// ambiguous, or rolled back) so that partial state is observable rather than inferred. A
    /// witness row (a marker
    /// written inside each source's transaction that a replay can read to learn what landed) would
    /// close this; it is deliberately not built here, since a single transactional source, the case
    /// every host runs today, needs none.
    /// </para>
    /// </remarks>
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        // Re-entrant call: join the transaction the outer call already opened instead of starting a
        // second one. Only the OUTERMOST call may begin, commit, roll back, or flush deferred events;
        // an inner commit would make the outer scope's earlier work durable ahead of its own
        // decision, turning a nested call into a silent partial commit. Without this, an
        // ITransactional command whose handler also opens a transaction (the shape Store's
        // CheckOutHandler and VerifyPaymentHandler avoid only by convention, with a comment on each
        // command saying so) hit an InvalidOperationException from EF instead.
        if (_transactionActive)
            return await operation(cancellationToken).ConfigureAwait(false);

        // Use the execution strategy from the first active transactional context
        // (typically SQL Server). If none exists yet, create the default context so
        // the strategy is available before the handler's first repository call. The engine goes
        // through the resolver rather than being taken literally: in a host that configures no SQL
        // Server connection this is the first thing an ITransactional command touches, and a
        // literal SQL Server context would open a connection string that does not exist.
        var context = _dbContexts.Values.FirstOrDefault(SupportsTransactions)
            ?? GetDbContext(_dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName));

        var attempt = 0;
        TransactionCommitAmbiguousException? commitFailure = null;
        var strategy = context.Database.CreateExecutionStrategy();

        var outcome = await strategy.ExecuteAsync(async ct =>
        {
            if (attempt++ > 0)
                ResetForRetry();

            var (value, failure) = await RunTransactionalAttemptAsync(operation, ct).ConfigureAwait(false);
            commitFailure = failure;
            return value;
        }, cancellationToken).ConfigureAwait(false);

        // Thrown out here, past the strategy, on purpose. The strategy decides retriability by
        // walking an exception's WHOLE inner chain, so a wrapper carrying the transient commit
        // error would still be retried; returning normally is the only way to guarantee the
        // delegate is not re-entered after a commit whose outcome is unknown.
        if (commitFailure is not null)
            throw commitFailure;

        return outcome;
    }

    /// <summary>
    /// Runs one attempt of the transactional unit: begin, operate, then commit or roll back.
    /// </summary>
    /// <returns>
    /// The operation's result, plus the commit failure when the commit phase threw. A commit
    /// failure is RETURNED rather than thrown so the execution strategy sees a completed attempt
    /// and cannot re-run the operation against a commit that may already be durable.
    /// </returns>
    private async Task<(TResult Value, TransactionCommitAmbiguousException? CommitFailure)> RunTransactionalAttemptAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        BeginTransaction();
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);

            if (result is Result { IsFailure: true })
            {
                // Business failure: atomicity over partial persistence. RollbackTransaction
                // also drops any deferred event dispatch (the events' outbox rows roll back
                // with the data, so nothing may be delivered).
                RollbackTransaction();
                return (result, null);
            }

            var commitFailure = TryCommit();
            if (commitFailure is not null)
                return (result, commitFailure);

            // Deliver events only now that the data is durable: in-process handlers must
            // never act on state that could still roll back. Snapshot first: a handler that
            // reaches a not-yet-materialized source adds to _dbContexts while we enumerate.
            foreach (var committedContext in _dbContexts.Values.ToArray())
            {
                await DomainEventSaveChangesInterceptor.FlushDeferredAsync(committedContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            return (result, null);
        }
        catch (OperationCanceledException)
        {
            // The connection may already be closed; best-effort rollback.
            // Disposal will clean up if this fails.
            try
            {
                RollbackTransaction();
            }
            catch
            {
                _transactionActive = false;
                foreach (var abortedContext in _dbContexts.Values.ToArray())
                    DomainEventSaveChangesInterceptor.DropDeferred(abortedContext);
            }

            throw;
        }
        catch
        {
            RollbackTransaction();
            throw;
        }
    }

    /// <summary>
    /// Commits every enlisted context in turn, returning the failure instead of throwing it and
    /// recording what each physical source did. Commits are sequential and independent (no
    /// two-phase commit), so a failure part-way through leaves earlier sources durable; naming them
    /// is what makes the partial state observable to the caller who owns the replay.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> on success, otherwise the ambiguity carrying the provider's failure
    /// and the per-source outcome.
    /// </returns>
    private TransactionCommitAmbiguousException? TryCommit()
    {
        _transactionActive = false;

        // Snapshot before committing anything: this order IS the commit order, so the entries past
        // the failing one are exactly what AbandonAfterCommitFailure rolls back.
        var enlisted = _dbContexts
            .Where(entry => SupportsTransactions(entry.Value) && HasActiveTransaction(entry.Value))
            .Select(entry => (Source: entry.Key.ToString(), Context: entry.Value))
            .ToArray();

        var committed = new List<string>(enlisted.Length);

        for (var index = 0; index < enlisted.Length; index++)
        {
            var (source, context) = enlisted[index];

            try
            {
                context.Database.CommitTransaction();
            }
            catch (Exception ex)
            {
                var rolledBack = enlisted[(index + 1)..].Select(entry => entry.Source).ToArray();
                AbandonAfterCommitFailure();
                return new TransactionCommitAmbiguousException(ex, committed, source, rolledBack);
            }

            committed.Add(source);
        }

        return null;
    }

    /// <summary>
    /// Best-effort cleanup after a commit whose outcome is unknown: roll back whatever has not
    /// committed yet (with multiple sources the commits are sequential, so later ones may still be
    /// open) and drop every context's deferred dispatch. The outbox rows are the only delivery
    /// record that survives an ambiguous commit, and the outbox processor delivers them if the
    /// commit did land.
    /// </summary>
    private void AbandonAfterCommitFailure()
    {
        _transactionActive = false;

        foreach (var context in _dbContexts.Values.ToArray())
        {
            DomainEventSaveChangesInterceptor.DropDeferred(context);

            if (!SupportsTransactions(context) || !HasActiveTransaction(context))
                continue;

            try
            {
                context.Database.RollbackTransaction();
            }
            catch
            {
                // The transaction is already zombied or the connection is gone. Swallowing keeps
                // the commit ambiguity as the reported failure; disposal releases what is left.
            }
        }
    }

    /// <inheritdoc />
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in GetMigrationTargets())
            await GetDbContext(key).Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The sources this host migrates: every source in use whose resolved
    /// <see cref="PhysicalDataSource.UsesMigrations"/> says a migrations pipeline owns its schema
    /// (every SQL Server source, plus a SQLite source with a configured migrations assembly).
    /// <para>
    /// A migration target that resolves no connection string is skipped unless it is SQL Server,
    /// which keeps two behaviours intact: an optional SQLite source a test host leaves unconfigured
    /// stays silently absent (exactly as <see cref="EnsureCreatedAsync"/> treats it), while a SQL
    /// Server source with no connection string still fails loudly at startup rather than being
    /// quietly skipped, because for SQL Server that is a misconfiguration, not an option.
    /// </para>
    /// </summary>
    /// <returns>The keys to migrate, in source-in-use order.</returns>
    private List<DataSourceKey> GetMigrationTargets()
    {
        var targets = new List<DataSourceKey>();

        foreach (var key in GetSourcesInUse())
        {
            var physical = _dataSourceResolver.GetPhysical(key);

            if (!physical.UsesMigrations)
                continue;

            if (key.Engine != DataSource.SQLServer && string.IsNullOrEmpty(physical.ConnectionString))
                continue;

            targets.Add(key);
        }

        return targets;
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in GetMigrationTargets())
        {
            var pending = await GetDbContext(key).Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Discards everything the previous, aborted attempt left behind before the execution strategy
    /// re-runs the operation: tracked entity state (so Added entities are not inserted once per
    /// attempt) and any deferred event dispatch.
    /// </summary>
    private void ResetForRetry()
    {
        foreach (var context in _dbContexts.Values.ToArray())
        {
            DomainEventSaveChangesInterceptor.DropDeferred(context);
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Cosmos DB does not support multi-document transactions via the EF provider;
    /// transaction operations are skipped for Cosmos contexts.
    /// </summary>
    private static bool SupportsTransactions(ApplicationDbContext context) =>
        context is not CosmosDbContext;

    private static bool HasActiveTransaction(ApplicationDbContext context) =>
        context.Database.CurrentTransaction is not null;

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (var context in _dbContexts.Values.ToArray())
                    context.Dispose();
                _dbContexts.Clear();
            }
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            foreach (var context in _dbContexts.Values.ToArray())
                await context.DisposeAsync().ConfigureAwait(false);
            _dbContexts.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
