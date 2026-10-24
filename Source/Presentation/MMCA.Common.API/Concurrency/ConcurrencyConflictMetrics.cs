using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;

namespace MMCA.Common.API.Concurrency;

/// <summary>
/// The optimistic-concurrency conflict counter: how often a save lost to a moved row version and the
/// request was answered with a conflict. A host exports it by registering the <see cref="MeterName"/>
/// meter, which the Aspire service defaults do. The meter name is duplicated as a literal in
/// MMCA.Common.Aspire because that package has no reference to this one.
/// </summary>
/// <remarks>
/// <para>
/// Recorded at the two places an HTTP request turns a <see cref="DbUpdateConcurrencyException"/> into
/// a response: <c>SupportsIfMatchAttribute</c> (412 under an explicit <c>If-Match</c>, and it marks
/// the exception handled) and <c>DbUpdateExceptionHandler</c> (409 for every other endpoint). A given
/// exception reaches exactly one of the two, so each conflict is counted once.
/// </para>
/// <para>
/// No alert ships on it. A sustained rate on one entity type points at contended rows (two editors,
/// or a background writer racing user edits), which is a design question, not an incident.
/// </para>
/// </remarks>
internal static class ConcurrencyConflictMetrics
{
    /// <summary>OpenTelemetry meter name for the persistence instruments.</summary>
    internal const string MeterName = "MMCA.Common.Persistence";

    /// <summary>Tag value when the exception carries no entry to name the entity type from.</summary>
    internal const string UnknownEntityType = "unknown";

    /// <summary>Tag name carrying the CLR type name of the entity whose save conflicted.</summary>
    private const string EntityTypeTag = "entity_type";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ConflictCounter = Meter.CreateCounter<long>(
        "persistence.concurrency.conflict",
        unit: "{conflict}",
        description: "Saves rejected by an optimistic-concurrency check and answered as a conflict, tagged by entity type.");

    /// <summary>
    /// Records one conflict, tagged with the entity type of the first entry the exception reports.
    /// </summary>
    /// <param name="exception">The concurrency exception being answered.</param>
    internal static void RecordConflict(DbUpdateConcurrencyException exception) =>
        ConflictCounter.Add(1, new KeyValuePair<string, object?>(EntityTypeTag, EntityTypeName(exception)));

    /// <summary>
    /// Names the conflicting entity type: the CLR type of the first entry, read from the EF model so a
    /// lazy-loading proxy reports its entity rather than the generated subclass.
    /// </summary>
    /// <param name="exception">The concurrency exception.</param>
    /// <returns>The entity type name, or <see cref="UnknownEntityType"/> when there is no entry.</returns>
    internal static string EntityTypeName(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count == 0 ? UnknownEntityType : exception.Entries[0].Metadata.ClrType.Name;
}
