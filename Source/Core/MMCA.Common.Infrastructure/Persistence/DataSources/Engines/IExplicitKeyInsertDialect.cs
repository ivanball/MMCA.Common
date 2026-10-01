using Microsoft.EntityFrameworkCore;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// The engine-specific half of an explicit-key insert (<c>RequestExplicitKeyInsert</c>). Only an
/// engine that refuses an explicit value for a store-generated key without a session toggle has one
/// (SQL Server, through <c>SET IDENTITY_INSERT</c>); every other engine returns
/// <see langword="null"/> from <see cref="IDataSourceEngine.ExplicitKeyInsert"/> and the save runs
/// unchanged. The engine-neutral half (grouping rounds, hiding other tables' entries, excluding their
/// domain events, pinning the connection) stays in <c>DbContextFactory</c>.
/// </summary>
internal interface IExplicitKeyInsertDialect
{
    /// <summary>
    /// Finds the added entries whose single-column key is store-generated on this engine and carries
    /// an explicit (non-temporary) value, grouped by target table.
    /// </summary>
    /// <param name="context">The context whose change tracker is scanned.</param>
    /// <returns>One group per table; empty when nothing needs the toggle.</returns>
    IReadOnlyList<ExplicitKeyInsertGroup> FindGroups(DbContext context);

    /// <summary>Builds the statement that switches explicit-key insert on or off for one table.</summary>
    /// <param name="schema">The table schema (from EF model metadata, never user input).</param>
    /// <param name="table">The table name (from EF model metadata, never user input).</param>
    /// <param name="enable"><see langword="true"/> to switch it on, <see langword="false"/> to switch it off.</param>
    /// <returns>The raw SQL statement.</returns>
    string BuildToggleSql(string schema, string table, bool enable);
}
