using System.ComponentModel.DataAnnotations;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.AuditTrail;

/// <summary>
/// Configuration for the entity change-history trail, bound from the <c>AuditTrail</c> section.
/// Every property has a default, so a host that opts in only needs <c>AuditTrail:Enabled</c>.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> is the single gate: it decides both whether changes are recorded and
/// whether the <c>AuditTrailEntries</c> table is mapped into the model at all. A host that leaves
/// it false has exactly the model it had before the trail existed, so its migrations never see the
/// table.
/// </remarks>
public sealed class AuditTrailSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "AuditTrail";

    /// <summary>
    /// Gets a value indicating whether changes to <c>IAuditedEntity</c> entities are recorded.
    /// Defaults to <see langword="false"/>: adopting the framework must never add a table or a
    /// write per change to a host that did not ask for one.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the number of days a trail row is kept before <c>AuditTrailCleanupJob</c> purges it.
    /// Defaults to <c>90</c>.
    /// </summary>
    /// <remarks>
    /// Retention only happens if the host also runs the scheduler (<c>AddScheduledJobs</c> plus
    /// <c>Scheduler:Enabled</c>). Without it the trail still records and this value is inert: the
    /// table grows until an operator prunes it.
    /// </remarks>
    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 90;

    /// <summary>
    /// Gets the engine whose <c>Default</c> database the read surface (<c>IAuditTrailReader</c>)
    /// queries. Trail rows are written to every relational source alongside the data they describe;
    /// this only says which one the v1 reader looks in. Defaults to
    /// <see cref="DataSource.SQLServer"/>. Cosmos DB is not a valid value (the table is relational).
    /// </summary>
    public DataSource DataSource { get; init; } = DataSource.SQLServer;
}
