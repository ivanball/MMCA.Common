namespace MMCA.Common.Application.Auditing;

/// <summary>
/// One recorded change in an entity's history, as returned by <c>IAuditTrailReader</c>.
/// </summary>
/// <remarks>
/// A read-side projection of the framework's trail row, deliberately minimal in v1: it carries what
/// a "who changed what, and when" view needs and nothing an infrastructure table happens to also
/// store. Values are the invariant string forms recorded at capture time, and a value that belonged
/// to a property marked as personal data reads as the redaction placeholder on both sides.
/// </remarks>
public sealed record AuditTrailEntryDTO
{
    /// <summary>Gets the identifier of the trail row.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the full CLR type name of the entity that changed.</summary>
    public required string EntityType { get; init; }

    /// <summary>Gets the invariant string form of the changed entity's primary key.</summary>
    public required string EntityKey { get; init; }

    /// <summary>
    /// Gets the name of the property that changed, or <see langword="null"/> on the summary row of
    /// a create or delete.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>Gets the value before the change, or <see langword="null"/>.</summary>
    public string? OldValue { get; init; }

    /// <summary>Gets the value after the change, or <see langword="null"/>.</summary>
    public string? NewValue { get; init; }

    /// <summary>Gets the operation: <c>Added</c>, <c>Modified</c> or <c>Deleted</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Gets the user the change was saved as, or <see langword="null"/> when the save carried no
    /// identity (a background service, a seeder).
    /// </summary>
    public UserIdentifierType? ChangedBy { get; init; }

    /// <summary>Gets the UTC instant the change was recorded.</summary>
    public required DateTime ChangedOn { get; init; }

    /// <summary>Gets the trace identifier the change was recorded under, or <see langword="null"/>.</summary>
    public string? CorrelationId { get; init; }
}
