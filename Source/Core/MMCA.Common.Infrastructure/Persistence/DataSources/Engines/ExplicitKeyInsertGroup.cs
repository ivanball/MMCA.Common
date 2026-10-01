using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

/// <summary>
/// The pending inserts into one table that carry explicit values for a store-generated key, and so
/// must be saved in their own round with the engine's explicit-key toggle switched on.
/// </summary>
/// <param name="Schema">The table schema.</param>
/// <param name="Table">The table name.</param>
/// <param name="Entries">The added entries that carry explicit key values.</param>
internal sealed record ExplicitKeyInsertGroup(string Schema, string Table, IReadOnlyList<EntityEntry> Entries);
