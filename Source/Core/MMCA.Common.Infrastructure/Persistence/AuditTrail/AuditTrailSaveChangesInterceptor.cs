using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Privacy;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Persistence.AuditTrail;

/// <summary>
/// EF Core interceptor that records a field-level change history for every entity marked with
/// <see cref="IAuditedEntity"/>, writing <see cref="AuditTrailEntry"/> rows in the <b>same
/// transaction</b> as the change they describe (the outbox precedent: a trail that can be committed
/// without its data, or the other way round, is worse than no trail).
/// </summary>
/// <remarks>
/// <para>
/// <b>Position in the pipeline.</b> Registered last, after
/// <see cref="Interceptors.AuditSaveChangesInterceptor"/> and
/// <see cref="Interceptors.DomainEventSaveChangesInterceptor"/>, so the values it captures are the
/// final ones: the audit stamps (<c>LastModifiedBy/On</c>) are already written when the diff runs.
/// Its own rows, and the framework's own bookkeeping tables, are never audited.
/// </para>
/// <para>
/// <b>Opt-in twice over.</b> Nothing happens unless the host called <c>AddAuditTrail</c> (this
/// interceptor is resolved with <c>GetService</c>, so its absence is a silent no-op) AND
/// <c>AuditTrail:Enabled</c> mapped the table into the model. Both are checked cheaply per save.
/// </para>
/// <para>
/// <b>Personal data never reaches the table.</b> A property carrying <see cref="PiiAttribute"/>
/// records <see cref="PiiRedactor.RedactedToken"/> on both sides of the change. Redaction happens
/// at capture, not at read, so the trail cannot become a second copy of a data subject's personal
/// data that erasure would have to chase (ADR-005).
/// </para>
/// <para>
/// <b>Correlation.</b> The row records the ambient <see cref="Activity"/> trace id, not the scoped
/// <c>ICorrelationContext</c>. This interceptor is a singleton, and the only service provider a
/// <see cref="ApplicationDbContext"/> carries is the ROOT provider (contexts are built by the
/// singleton <c>PhysicalDbContextFactory</c>), so a scoped service is not reachable from here and
/// capturing one would be a lifetime bug rather than a feature. The trace id is ambient, is what
/// the telemetry pipeline correlates on, and is the same value
/// <c>CorrelationIdMiddleware</c> itself falls back to when a request carries no
/// <c>X-Correlation-ID</c> header. A caller-supplied header value is therefore NOT recorded in v1;
/// honoring it needs a live accessor on the context assigned by the scoped factory (the shape the
/// multi-tenancy work introduces for <c>TenantId</c>), which is deliberately out of scope here.
/// </para>
/// </remarks>
/// <param name="timeProvider">Provides the UTC capture timestamp.</param>
public sealed class AuditTrailSaveChangesInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    /// <summary>Operation recorded for an inserted entity (one summary row).</summary>
    internal const string OperationAdded = "Added";

    /// <summary>Operation recorded for an updated entity (one row per changed property).</summary>
    internal const string OperationModified = "Modified";

    /// <summary>Operation recorded for a hard-deleted entity (one summary row).</summary>
    internal const string OperationDeleted = "Deleted";

    /// <summary>Column width of <c>EntityType</c>; longer names are truncated to fit.</summary>
    internal const int MaxEntityTypeLength = 256;

    /// <summary>Column width of <c>EntityKey</c> and <c>PropertyName</c>; longer values are truncated to fit.</summary>
    internal const int MaxKeyLength = 128;

    /// <summary>Column width of <c>CorrelationId</c>; longer values are truncated to fit.</summary>
    internal const int MaxCorrelationIdLength = 64;

    /// <summary>Separator joining the parts of a composite primary key into one <c>EntityKey</c> value.</summary>
    internal const char KeySeparator = '|';

    /// <summary>
    /// Trail rows whose <see cref="AuditTrailEntry.EntityKey"/> could not be known before the save,
    /// keyed by context. Used only by the store-generated-key fix-up below.
    /// </summary>
    private static readonly ConditionalWeakTable<DbContext, List<PendingEntityKey>> PendingKeyTable = [];

    /// <summary>
    /// Marks a context whose capture has staged rows but whose save has not completed. Present only
    /// between <c>SavingChanges</c> and <c>SavedChanges</c>, so the discard below can tell an
    /// abandoned attempt from rows someone else legitimately added.
    /// </summary>
    private static readonly ConditionalWeakTable<DbContext, object> CaptureMarkerTable = [];

    /// <summary>The value stored in <see cref="CaptureMarkerTable"/>; only its presence matters.</summary>
    private static readonly object CaptureMarker = new();

    /// <summary>Caches the <see cref="PiiAttribute"/> verdict per (declaring type, property name).</summary>
    private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName), bool> PiiCache = new();

    /// <summary>
    /// The framework's own bookkeeping entities, which are never audited whatever markers they
    /// carry. Guarding by CLR type rather than by the absence of <see cref="IAuditedEntity"/> keeps
    /// the trail from ever recording its own rows (an unbounded feedback loop) and keeps the outbox,
    /// inbox and job tables out of a history nobody asked for.
    /// </summary>
    private static readonly HashSet<Type> FrameworkEntityTypes =
    [
        typeof(AuditTrailEntry),
        typeof(OutboxMessage),
        typeof(InboxMessage),
        typeof(ScheduledJobEntry),
    ];

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext context)
            CaptureChanges(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is ApplicationDbContext context)
            CaptureChanges(context);

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext context)
            await ResolveGeneratedKeysAsync(context, cancellationToken).ConfigureAwait(false);

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is ApplicationDbContext context)
            ResolveGeneratedKeys(context);

        return base.SavedChanges(eventData, result);
    }

    /// <summary>
    /// Indicates whether <paramref name="type"/> is one of the framework's own bookkeeping entities,
    /// which the trail never records.
    /// </summary>
    /// <param name="type">The entity CLR type to test.</param>
    /// <returns><see langword="true"/> when the type must never be audited.</returns>
    internal static bool IsFrameworkEntity(Type type) => FrameworkEntityTypes.Contains(type);

    /// <summary>
    /// Walks the change tracker and adds one <see cref="AuditTrailEntry"/> per changed property
    /// (<c>Modified</c>) or one summary row (<c>Added</c> / <c>Deleted</c>).
    /// </summary>
    private void CaptureChanges(ApplicationDbContext context)
    {
        // Two gates in one lookup: a host that never opted in has no AuditTrailEntry in its model,
        // and neither does Cosmos (which skips the relational tables entirely). Set<AuditTrailEntry>
        // would throw for both, so nothing may run before this check.
        if (context.Model.FindEntityType(typeof(AuditTrailEntry)) is null)
        {
            return;
        }

        DiscardAbandonedCapture(context);

        var capture = new CaptureContext(
            context.CurrentSaveUserId,
            timeProvider.GetUtcNow().UtcDateTime,
            Truncate(Activity.Current?.TraceId.ToString(), MaxCorrelationIdLength));

        // Snapshot before adding: the rows this method writes land in the same tracker, and
        // enumerating it lazily while adding to it would throw.
        var entries = context.ChangeTracker.Entries().Where(ShouldAudit).ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        List<PendingEntityKey>? pending = null;

        foreach (var entry in entries)
        {
            if (CaptureEntry(context, entry, capture) is { } pendingRow)
            {
                (pending ??= []).Add(pendingRow);
            }
        }

        if (pending is not null)
        {
            PendingKeyTable.AddOrUpdate(context, pending);
        }

        CaptureMarkerTable.AddOrUpdate(context, CaptureMarker);
    }

    /// <summary>
    /// Whether a tracked entry belongs in the trail: it opted in, it is not one of the framework's
    /// own bookkeeping entities, and it is actually being written.
    /// </summary>
    private static bool ShouldAudit(EntityEntry entry) =>
        entry.Entity is IAuditedEntity
        && !IsFrameworkEntity(entry.Metadata.ClrType)
        && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    /// <summary>
    /// Records one changed entry: a row per changed property for an update, one summary row for an
    /// insert or a delete.
    /// </summary>
    /// <returns>
    /// The inserted row awaiting a store-generated key, or <see langword="null"/> when the key was
    /// already known (or the entry was not an insert).
    /// </returns>
    private static PendingEntityKey? CaptureEntry(
        ApplicationDbContext context,
        EntityEntry entry,
        CaptureContext capture)
    {
        var entityType = Truncate(entry.Metadata.ClrType.FullName ?? entry.Metadata.ClrType.Name, MaxEntityTypeLength)!;
        var entityKey = BuildEntityKey(entry);

        if (entry.State == EntityState.Modified)
        {
            CaptureModifiedProperties(context, entry, entityType, entityKey, capture);
            return null;
        }

        var row = AddRow(context, new AuditTrailEntry
        {
            EntityType = entityType,
            EntityKey = entityKey,
            Operation = entry.State == EntityState.Added ? OperationAdded : OperationDeleted,
            ChangedBy = capture.ChangedBy,
            ChangedOn = capture.ChangedOn,
            CorrelationId = capture.CorrelationId,
        });

        // A store-generated key (the framework's identity columns) does not exist yet at this point:
        // EF holds a temporary sentinel that would make the row unfindable by key. Remember it and
        // rewrite the column once the insert has assigned the real value, inside the same
        // transaction when one is open.
        return entry.State == EntityState.Added && HasTemporaryKey(entry)
            ? new PendingEntityKey(row, entry)
            : null;
    }

    /// <summary>
    /// Adds one row per property whose value actually changed. A property EF flagged as modified but
    /// whose value is unchanged (the whole-entity <c>Update</c> idiom) writes nothing: a trail that
    /// records non-changes is noise, and the volume is the reason the feature is opt-in per entity.
    /// </summary>
    private static void CaptureModifiedProperties(
        ApplicationDbContext context,
        EntityEntry entry,
        string entityType,
        string entityKey,
        CaptureContext capture)
    {
        // One reflection pass per entity type, cached by the redactor: when nothing on the type is
        // personal data, the per-property attribute lookup below is skipped entirely.
        var typeHasPii = PiiRedactor.HasPii(entry.Metadata.ClrType);

        foreach (var property in entry.Properties)
        {
            if (!property.IsModified)
            {
                continue;
            }

            var original = property.OriginalValue;
            var current = property.CurrentValue;
            if (ValuesEqual(original, current))
            {
                continue;
            }

            var isPii = typeHasPii && IsPiiProperty(property);

            AddRow(context, new AuditTrailEntry
            {
                EntityType = entityType,
                EntityKey = entityKey,
                PropertyName = Truncate(property.Metadata.Name, MaxKeyLength),
                OldValue = isPii ? PiiRedactor.RedactedToken : FormatValue(original),
                NewValue = isPii ? PiiRedactor.RedactedToken : FormatValue(current),
                Operation = OperationModified,
                ChangedBy = capture.ChangedBy,
                ChangedOn = capture.ChangedOn,
                CorrelationId = capture.CorrelationId,
            });
        }
    }

    /// <summary>
    /// Tracks one trail row for insertion in the current save. Mutation is <c>Add</c> only: the save
    /// runs under <c>DetectChangesOnce</c> with automatic detection off, and <c>Add</c> takes effect
    /// on the entry immediately.
    /// </summary>
    private static AuditTrailEntry AddRow(ApplicationDbContext context, AuditTrailEntry row)
    {
#pragma warning disable VSTHRD103 // EF DbSet.Add is intentionally synchronous (in-memory); AddAsync is only for special value generators (EF guidance).
        context.Set<AuditTrailEntry>().Add(row);
#pragma warning restore VSTHRD103
        return row;
    }

    /// <summary>
    /// Detaches the trail rows written by a capture whose save never completed. An execution
    /// strategy that retries a failed save re-runs <c>SavingChanges</c> against a tracker that still
    /// holds the previous attempt's Added rows, so without this a retried operation writes one trail
    /// row per attempt.
    /// </summary>
    private static void DiscardAbandonedCapture(ApplicationDbContext context)
    {
        // Only when a previous capture staged rows and never reached SavedChanges. Discarding
        // unconditionally would also throw away trail rows a caller added deliberately (a data
        // import, a test fixture), which is not this method's business.
        if (!CaptureMarkerTable.TryGetValue(context, out _))
        {
            return;
        }

        CaptureMarkerTable.Remove(context);
        PendingKeyTable.Remove(context);

        // Every Added AuditTrailEntry left on this context came from that abandoned capture: a
        // completed save leaves none Added.
        foreach (var entry in context.ChangeTracker.Entries<AuditTrailEntry>()
            .Where(e => e.State == EntityState.Added)
            .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Rewrites the <c>EntityKey</c> of rows captured before a store-generated key existed, now that
    /// the insert has assigned one. A single set-based <c>UPDATE</c> per row, exactly like
    /// <c>OutboxFinalizer</c>: it bypasses the change tracker and the interceptor pipeline, and it
    /// joins the ambient transaction when the save runs inside one.
    /// </summary>
    private static async Task ResolveGeneratedKeysAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        // The save completed, so nothing is abandoned any more.
        CaptureMarkerTable.Remove(context);

        if (!PendingKeyTable.TryGetValue(context, out var pending))
        {
            return;
        }

        PendingKeyTable.Remove(context);

        foreach (var item in pending)
        {
            var resolvedKey = BuildEntityKey(item.Entry);
            if (string.Equals(resolvedKey, item.Row.EntityKey, StringComparison.Ordinal))
            {
                continue;
            }

            var rowId = item.Row.Id;
            await context.Set<AuditTrailEntry>()
                .Where(r => r.Id == rowId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.EntityKey, resolvedKey), cancellationToken)
                .ConfigureAwait(false);

            SyncTrackedKey(context, item.Row, resolvedKey);
        }
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="ResolveGeneratedKeysAsync"/> for the
    /// <c>SaveChanges</c> path.
    /// </summary>
    private static void ResolveGeneratedKeys(ApplicationDbContext context)
    {
        // The save completed, so nothing is abandoned any more.
        CaptureMarkerTable.Remove(context);

        if (!PendingKeyTable.TryGetValue(context, out var pending))
        {
            return;
        }

        PendingKeyTable.Remove(context);

        foreach (var item in pending)
        {
            var resolvedKey = BuildEntityKey(item.Entry);
            if (string.Equals(resolvedKey, item.Row.EntityKey, StringComparison.Ordinal))
            {
                continue;
            }

            var rowId = item.Row.Id;
            context.Set<AuditTrailEntry>()
                .Where(r => r.Id == rowId)
                .ExecuteUpdate(s => s.SetProperty(r => r.EntityKey, resolvedKey));

            SyncTrackedKey(context, item.Row, resolvedKey);
        }
    }

    /// <summary>
    /// Brings the tracked instance and its snapshot in line with the value just written by
    /// <c>ExecuteUpdate</c>, so a later save on the same context does not re-issue the update. The
    /// original-value write must precede clearing <c>IsModified</c>: clearing the flag reverts the
    /// current value to the original.
    /// </summary>
    private static void SyncTrackedKey(ApplicationDbContext context, AuditTrailEntry row, string resolvedKey)
    {
        var property = context.Entry(row).Property(nameof(AuditTrailEntry.EntityKey));
        row.EntityKey = resolvedKey;
        property.OriginalValue = resolvedKey;
        property.IsModified = false;
    }

    /// <summary>
    /// Whether any part of this entry's primary key is still a temporary value, i.e. the database
    /// assigns it during the insert.
    /// </summary>
    private static bool HasTemporaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return false;
        }

        foreach (var property in key.Properties)
        {
            if (entry.Property(property.Name).IsTemporary)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders the entry's primary key as one invariant string, joining the parts of a composite key
    /// with <see cref="KeySeparator"/> in the model's key order. A keyless entity yields an empty
    /// string rather than throwing: it is still worth recording that it changed.
    /// </summary>
    private static string BuildEntityKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return string.Empty;
        }

        var parts = key.Properties
            .Select(property => FormatValue(entry.Property(property.Name).CurrentValue) ?? string.Empty);

        return Truncate(string.Join(KeySeparator, parts), MaxKeyLength)!;
    }

    /// <summary>Whether the CLR property behind an EF property carries <see cref="PiiAttribute"/>.</summary>
    /// <remarks>
    /// A shadow property has no <see cref="System.Reflection.PropertyInfo"/> and therefore cannot
    /// carry the attribute, so it is never treated as personal data.
    /// </remarks>
    private static bool IsPiiProperty(PropertyEntry property)
    {
        var info = property.Metadata.PropertyInfo;
        if (info?.DeclaringType is null)
        {
            return false;
        }

        return PiiCache.GetOrAdd(
            (info.DeclaringType, info.Name),
            static (_, propertyInfo) => propertyInfo.IsDefined(typeof(PiiAttribute), inherit: false),
            info);
    }

    /// <summary>
    /// Renders a value for storage. Culture-invariant on purpose: a trail read years later, or on a
    /// replica with a different locale, must show the value that was written.
    /// </summary>
    private static string? FormatValue(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Compares two property values structurally. Byte arrays (row versions, hashes) need explicit
    /// handling: reference equality would report every save as a change.
    /// </summary>
    private static bool ValuesEqual(object? original, object? current)
    {
        if (original is null || current is null)
        {
            return original is null && current is null;
        }

        if (original is byte[] originalBytes && current is byte[] currentBytes)
        {
            return originalBytes.AsSpan().SequenceEqual(currentBytes);
        }

        return original.Equals(current);
    }

    /// <summary>Truncates a value to a column width, preserving null.</summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>The values every row of one save shares.</summary>
    /// <param name="ChangedBy">The user the save runs as, or null.</param>
    /// <param name="ChangedOn">The UTC capture instant.</param>
    /// <param name="CorrelationId">The ambient trace identifier, or null.</param>
    private readonly record struct CaptureContext(
        UserIdentifierType? ChangedBy,
        DateTime ChangedOn,
        string? CorrelationId);

    /// <summary>A trail row awaiting the store-generated key of the entity it describes.</summary>
    /// <param name="Row">The tracked trail row whose <c>EntityKey</c> must be rewritten.</param>
    /// <param name="Entry">The audited entry whose key the database assigns during the insert.</param>
    private sealed record PendingEntityKey(AuditTrailEntry Row, EntityEntry Entry);
}
