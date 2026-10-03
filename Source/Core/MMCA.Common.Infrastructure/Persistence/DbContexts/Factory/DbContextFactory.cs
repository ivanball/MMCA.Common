using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
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
/// The scope's tenant. Read live by every context this factory creates; a container that never
/// adopts tenancy still resolves it, and its <c>TenantId</c> is simply never set.
/// </param>
/// <param name="tenancySettings">
/// Bound tenancy configuration, consulted only for per-tenant connection overrides.
/// </param>
/// <param name="correlationContext">
/// The scope's correlation id, captured onto every outbox row written through a context this
/// factory creates. Defaulted, so a container that never registered one (a bare test provider) keeps
/// the previous constructor shape and simply stores no correlation id.
/// </param>
public sealed class DbContextFactory(
    IPhysicalDbContextFactory physicalDbContextFactory,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    ICurrentUserService currentUserService,
    ITenantContext tenantContext,
    IOptions<TenancySettings> tenancySettings,
    ICorrelationContext? correlationContext = null
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
    /// When <see langword="true"/>, <see cref="SaveChangesAsync"/> lets each context's engine find
    /// the Added entities carrying explicit store-generated key values and switch its explicit-key
    /// toggle on per table where the engine needs one. Reset after each save.
    /// </summary>
    private bool _explicitKeyInsertRequested;

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
                _routedContextTenants[dataSourceKey] = tenantContext.TenantId;

            AttachScopeAccessors(context);

            // Enlist late-created contexts in the active transaction so that all
            // persistence within a transactional command shares the same boundary.
            if (_transactionActive && SupportsTransactions(context))
                context.Database.BeginTransaction();
        }
        else
        {
            // A routed context is bound to one tenant's database; hand it back only to that tenant.
            GuardRoutedTenantUnchanged(dataSourceKey);

            // And a shared context created before the tenant resolved must not be handed to a
            // tenant that overrides this source.
            GuardSharedContextNotRoutable(dataSourceKey);
        }

        return context;
    }

    /// <summary>
    /// Gives a freshly created context a live view of the scope it belongs to: the tenant it runs
    /// as, and the ambient context its outbox rows must carry. Accessors, not copied values: the
    /// context can be created before the request's tenant or principal is resolved, and both the
    /// query filter and the outbox capture must read the answer that holds at query and save time
    /// rather than at construction time.
    /// </summary>
    /// <remarks>
    /// Written as a method taking a non-nullable parameter, and null-guarding inside, so the null
    /// tolerance a test double needs (a mocked physical factory can hand back no context at all)
    /// does not leak a maybe-null flow state back into the caller.
    /// </remarks>
    private void AttachScopeAccessors(ApplicationDbContext context)
    {
        if (context is null)
            return;

        context.TenantIdAccessor = () => tenantContext.TenantId;

        // Invoked once per save that writes outbox rows, not once per row: the interceptor reads it
        // before its capture loop, so flattening the role claims costs one pass per save.
        context.OutboxOriginAccessor = () => new OutboxOrigin(
            _currentUserService.UserId,
            AmbientOrigin.FlattenRoles(_currentUserService.Roles),
            tenantContext.TenantId,
            correlationContext?.CorrelationId);
    }

    /// <summary>
    /// The tenant's own connection information for one source, or <see langword="null"/> when this
    /// source stays shared. The clone keeps the ORIGINAL <see cref="DataSourceKey"/>: the key is
    /// what EF's model cache is keyed on, so replacing only the connection string is what lets one
    /// compiled model serve every tenant's database.
    /// </summary>
    private PhysicalDataSource? ResolveTenantOverride(DataSourceKey dataSourceKey)
    {
        if (tenantContext.TenantId is not { } tenantId
            || tenancySettings.Value is not { } settings
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

        var current = tenantContext.TenantId;
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

    /// <summary>
    /// Refuses to hand back a context bound to the SHARED database once the scope's tenant turns out
    /// to override this source. That happens when a repository call runs before the tenant is
    /// resolved (the context is created shared) and the tenant is set afterwards: serving it would
    /// read and write the shared database for a tenant that owns its own.
    /// </summary>
    private void GuardSharedContextNotRoutable(DataSourceKey dataSourceKey)
    {
        if (_routedContextTenants.ContainsKey(dataSourceKey) || ResolveTenantOverride(dataSourceKey) is null)
            return;

        throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            "The context for \"{0}\" was created before tenant \"{1}\" was resolved and is bound to the shared database, "
            + "but this tenant overrides the source. Resolve the tenant before the first repository call or use a fresh scope.",
            dataSourceKey,
            tenantContext.TenantId));
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
    /// When <see cref="RequestExplicitKeyInsert"/> has been called and a context's engine has an
    /// explicit-key insert dialect (SQL Server: <c>SET IDENTITY_INSERT ON/OFF</c>), scans that
    /// context's change tracker for entities with explicit store-generated key values and splits the
    /// save into one round per table with the toggle switched on.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table and schema names are derived from EF model metadata, not user input.")]
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var explicitKeyInsertRequested = _explicitKeyInsertRequested;
        _explicitKeyInsertRequested = false;

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

                result += explicitKeyInsertRequested && context.Engine.ExplicitKeyInsert is { } dialect
                    ? await SaveWithExplicitKeyInsertAsync(context, dialect, cancellationToken).ConfigureAwait(false)
                    : await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
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
    public void RequestExplicitKeyInsert() => _explicitKeyInsertRequested = true;

    /// <summary>
    /// Saves changes for a context whose engine needs a per-table toggle before it accepts explicit
    /// values for a store-generated key. Groups such entities by table and saves each group
    /// separately with the toggle on, respecting SQL Server's constraint that only one table may have
    /// <c>IDENTITY_INSERT ON</c> at a time per session.
    /// </summary>
    private async Task<int> SaveWithExplicitKeyInsertAsync(
        ApplicationDbContext context,
        IExplicitKeyInsertDialect dialect,
        CancellationToken cancellationToken)
    {
        var explicitKeyGroups = dialect.FindGroups(context);

        if (explicitKeyGroups.Count == 0)
            return await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);

        // One round per table, principals first: the change tracker's order is the order rows were
        // added, which says nothing about foreign keys (see ExplicitKeyInsertRoundOrder).
        var saveOrder = ExplicitKeyInsertRoundOrder.Order(
            [.. explicitKeyGroups.Select(g => (IReadOnlyCollection<IReadOnlyEntityType>)[.. g.Entries.Select(e => e.Metadata).Distinct()])]);
        explicitKeyGroups = [.. saveOrder.Select(i => explicitKeyGroups[i])];

        int result = 0;
        var allExplicitKeyEntries = explicitKeyGroups.SelectMany(g => g.Entries).ToHashSet();

        // The toggle is SESSION state. With no ambient transaction EF opens and closes the
        // connection around every command, so nothing guarantees the INSERT runs on the session
        // that ran the toggle (the connection goes back to the pool in between). Pin one session for
        // the whole save by opening the connection here; an already-open connection (an ambient
        // transaction, or a caller that opened it) is left exactly as it was found.
        var openedConnection = false;
        if (context.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            openedConnection = true;
        }

        try
        {
            result += await SaveExplicitKeyGroupsAsync(context, dialect, explicitKeyGroups, allExplicitKeyEntries, cancellationToken).ConfigureAwait(false);

            // Final save for any remaining changes (entities without explicit keys, updates, etc.)
            if (context.ChangeTracker.HasChanges())
            {
                result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedConnection)
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Saves each explicit-key group in its own round with the engine's toggle switched on, then
    /// off. The caller pins the connection so every round's toggle and INSERT share one session.
    /// </summary>
    private async Task<int> SaveExplicitKeyGroupsAsync(
        ApplicationDbContext context,
        IExplicitKeyInsertDialect dialect,
        IReadOnlyList<ExplicitKeyInsertGroup> explicitKeyGroups,
        HashSet<EntityEntry> allExplicitKeyEntries,
        CancellationToken cancellationToken)
    {
        int result = 0;

        foreach (var group in explicitKeyGroups)
        {
            // Temporarily hide entries from OTHER explicit-key tables so they are not included in
            // this round's batch (avoids the one-table-at-a-time constraint).
            var savedStates = allExplicitKeyEntries.Except(group.Entries)
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

            // try/finally: a failed save must not leave the toggle on for the pooled connection,
            // nor leave the hidden entries stuck in the Unchanged state (they would be silently
            // dropped from any retried save).
            try
            {
#pragma warning disable S2077 // Schema/table identifiers come from EF model metadata (entityType.GetSchema()/GetTableName()), not user input, and the toggle statement cannot take a parameterized identifier
                await context.Database.ExecuteSqlRawAsync(
                    dialect.BuildToggleSql(group.Schema, group.Table, enable: true),
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    result += await context.SaveChangesAsync(_currentUserService.UserId, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await context.Database.ExecuteSqlRawAsync(
                        dialect.BuildToggleSql(group.Schema, group.Table, enable: false),
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

        return result;
    }

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
        var committed = _dbContexts.Values.Where(SupportsTransactions).Where(HasActiveTransaction).ToArray();
        foreach (var context in committed)
            context.Database.CommitTransaction();

        // Internal commands enrolled during the transaction are durable only now, so this is the
        // first moment waking the processor finds them.
        EnrolledCommandWake.Release(committed);
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
            DropDeferredWork(context);
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
    /// Internal commands scheduled inside the transaction wake the internal-command processor once,
    /// right after a successful commit, so they run immediately rather than at the next poll. A
    /// rollback, an ambiguous commit or a retry drops the owed wake.
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

            // A throw from here is caught below and rolls the whole unit back.
            await FlushEnrolledCommandsBeforeCommitAsync(cancellationToken).ConfigureAwait(false);

            var commitFailure = TryCommit();
            if (commitFailure is not null)
                return (result, commitFailure);

            // Wake the processor for internal commands enrolled in this unit: they are durable only
            // now, and without the wake they would wait for the next poll. Released before the event
            // flush so a throwing in-process handler cannot swallow it.
            EnrolledCommandWake.Release([.. _dbContexts.Values]);

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
                    DropDeferredWork(abortedContext);
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
    /// Applies the commit rule to whatever the operation left tracked but unsaved. An internal
    /// command scheduled while the transaction is open is only ENROLLED on the context (it commits
    /// with the caller's change or not at all), so when every pending entry is such an enrolled row
    /// the unit is saved here, inside the transaction, and the row commits with the rest. Any other
    /// unsaved change throws: committing would silently discard it while the unit reports success,
    /// the same loss <see cref="SaveChangesAsync"/> already refuses at the end of a save.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the flush.</returns>
    private async Task FlushEnrolledCommandsBeforeCommitAsync(CancellationToken cancellationToken)
    {
        var dirty = _dbContexts
            .Where(entry => entry.Value.ChangeTracker.HasChanges())
            .ToArray();

        if (dirty.Length == 0)
            return;

        var unsavedOther = dirty
            .SelectMany(entry => entry.Value.ChangeTracker.Entries()
                .Where(e => e.State is not EntityState.Unchanged and not EntityState.Detached
                    && !(e.State == EntityState.Added && e.Entity is InternalCommandMessage))
                .Select(e => $"{entry.Key}: {e.Metadata.ClrType.Name} ({e.State})"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (unsavedOther.Length > 0)
        {
            throw new InvalidOperationException(
                "The transactional unit finished with unsaved changes still tracked: "
                + string.Join(", ", unsavedOther)
                + ". Committing would have discarded them while reporting success; save them before the operation returns.");
        }

        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            DropDeferredWork(context);

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
    /// <see cref="PhysicalDataSource.IsMigrationTarget"/> says a migrations pipeline owns its schema
    /// (every SQL Server source, plus a SQLite or PostgreSQL source with a configured migrations
    /// assembly).
    /// <para>
    /// A migration target that resolves no connection string is skipped unless its engine requires
    /// one (SQL Server), which keeps two behaviours intact: an optional SQLite source a test host leaves unconfigured
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
            if (!_dataSourceResolver.GetPhysical(key).IsMigrationTarget)
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
            DropDeferredWork(context);
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Forgets everything owed to a transaction that did not commit: the deferred in-process event
    /// dispatch and the processor wake owed by internal commands enrolled in it.
    /// </summary>
    /// <param name="context">The context whose transaction rolled back or was abandoned.</param>
    private static void DropDeferredWork(ApplicationDbContext context)
    {
        DomainEventSaveChangesInterceptor.DropDeferred(context);
        EnrolledCommandWake.Drop(context);
    }

    /// <summary>
    /// Only a relational engine supports database transactions through the EF provider (Cosmos DB
    /// has no multi-document transactions); transaction operations are skipped for the others.
    /// </summary>
    private static bool SupportsTransactions(ApplicationDbContext context) =>
        context.Engine.Capabilities.IsRelational;

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
