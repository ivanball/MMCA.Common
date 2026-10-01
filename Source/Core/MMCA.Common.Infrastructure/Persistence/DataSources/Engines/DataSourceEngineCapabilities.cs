namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// What one engine can and cannot do, as plain values a call site reads instead of naming an engine.
/// Only facts whose values genuinely differ between engines get their own member; everything that
/// splits the engines exactly along the relational / non-relational line is the single
/// <see cref="IsRelational"/> flag.
/// </summary>
/// <param name="Migrations">Whether a source on this engine is migrated or created outright.</param>
/// <param name="ConnectionStringRequired">
/// A source on this engine with no connection string is a startup misconfiguration rather than an
/// optional source to skip (SQL Server only).
/// </param>
/// <param name="IsRelational">
/// The engine is relational. Every relational engine supports, and the non-relational one (Cosmos)
/// does not: EF <c>.Include()</c> between two entities of the same physical source, database
/// transactions, raw parameterized SQL (<c>Database.SqlQuery</c>), indexes (filtered ones included),
/// foreign-key constraints whose delete behavior matters, LINQ <c>Any(predicate)</c> translation, and
/// the framework's own relational tables (outbox, inbox, internal commands, scheduled jobs).
/// </param>
/// <param name="NullsSortFirstAscending">The engine sorts nulls first in an ascending sort (keyset pagination depends on it).</param>
/// <param name="RowVersion">How the <c>RowVersion</c> concurrency token is mapped and maintained.</param>
internal sealed record DataSourceEngineCapabilities(
    MigrationPolicy Migrations,
    bool ConnectionStringRequired,
    bool IsRelational,
    bool NullsSortFirstAscending,
    RowVersionStrategy RowVersion);
