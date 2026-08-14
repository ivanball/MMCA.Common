namespace MMCA.Common.Infrastructure.Persistence.AuditTrail;

/// <summary>
/// One recorded change to an entity marked with <c>IAuditedEntity</c>, written in the same
/// transaction as the change it describes by <see cref="AuditTrailSaveChangesInterceptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> an <c>IAuditableEntity</c>, exactly like <c>OutboxMessage</c> and
/// <c>ScheduledJobEntry</c>: this is framework bookkeeping, not domain state. It carries no
/// soft-delete flag (so no global query filter applies to it), no audit stamps of its own, and no
/// concurrency token. Rows are append-only: nothing in the framework ever updates one, and the only
/// deletion is the retention sweep in <see cref="AuditTrailCleanupJob"/>.
/// </para>
/// <para>
/// <b>Row shape.</b> A <c>Modified</c> save produces one row per property whose value actually
/// changed, so a trail reads as a field-level history. An <c>Added</c> or <c>Deleted</c> save
/// produces a single summary row with a null <see cref="PropertyName"/>: the interesting fact there
/// is the lifecycle event, and one row per column at insert time would multiply the table for no
/// extra information (the created state is already the first Modified baseline).
/// </para>
/// </remarks>
public sealed class AuditTrailEntry
{
    /// <summary>Gets the unique identifier for this trail row.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets the full CLR type name of the entity that changed. Stored as a string rather than a
    /// foreign key because the trail outlives the row it describes (a deleted entity keeps its
    /// history) and spans every audited type in one table.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// Gets or sets the invariant string form of the changed entity's primary key. A composite key
    /// is joined with <c>|</c> in the model's key order, so one column addresses every audited
    /// entity regardless of its key shape.
    /// </summary>
    /// <remarks>
    /// Settable, unlike every other property here, for exactly one reason: an entity with a
    /// store-generated key (the framework's identity columns) has no key yet when the change is
    /// captured, so <see cref="AuditTrailSaveChangesInterceptor"/> rewrites this one column once the
    /// insert has assigned it. Nothing else ever changes a trail row.
    /// </remarks>
    public required string EntityKey { get; set; }

    /// <summary>
    /// Gets the name of the property that changed, or <see langword="null"/> on the summary row of
    /// an <c>Added</c> or <c>Deleted</c> operation.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// Gets the invariant string form of the value before the change, or <see langword="null"/>
    /// when the property was null, when the operation has no before-state (<c>Added</c>), or when
    /// the property carries <c>PiiAttribute</c> (in which case it holds the redaction placeholder).
    /// </summary>
    public string? OldValue { get; init; }

    /// <summary>
    /// Gets the invariant string form of the value after the change, or <see langword="null"/>
    /// when the property became null, when the operation has no after-state (<c>Deleted</c>), or
    /// when the property carries <c>PiiAttribute</c> (in which case it holds the redaction
    /// placeholder).
    /// </summary>
    public string? NewValue { get; init; }

    /// <summary>
    /// Gets the operation that produced this row: <c>Added</c>, <c>Modified</c> or <c>Deleted</c>.
    /// A soft delete arrives here as <c>Modified</c> on <c>IsDeleted</c>, which is exactly what it
    /// is at the database level.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Gets the identifier of the user the save ran as, or <see langword="null"/> when the save
    /// carried no identity (a background service, a seeder, a plain <c>SaveChangesAsync</c>).
    /// </summary>
    public UserIdentifierType? ChangedBy { get; init; }

    /// <summary>Gets the UTC instant the change was captured.</summary>
    public DateTime ChangedOn { get; init; }

    /// <summary>
    /// Gets the ambient trace identifier the change was captured under, or <see langword="null"/>
    /// when the save ran outside any activity. It ties a trail row back to the request or job in
    /// the telemetry pipeline. See <see cref="AuditTrailSaveChangesInterceptor"/> for why this is a
    /// trace id rather than the scoped <c>ICorrelationContext</c> value.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Gets the tenant the change was made under, or <see langword="null"/> when the save resolved
    /// no tenant (a background service, a seeder, a single-tenant host). Recorded from
    /// <c>ApplicationDbContext.CurrentTenantId</c> at capture, so a trail row can be read back per
    /// tenant even where the trail table itself is shared.
    /// </summary>
    public string? TenantId { get; init; }
}
