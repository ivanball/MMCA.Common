using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Orders the explicit-key insert rounds of one save so a principal table is written before every
/// table that references it.
/// <para>
/// Each round writes one table while the other rounds' added rows are hidden, so EF's own
/// per-command ordering cannot help across rounds: a dependent table saved first inserts rows whose
/// foreign key points at a principal row that does not exist yet, and the save fails with a foreign
/// key violation. The order comes from the EF model's foreign keys, never from the order entities
/// were added to the change tracker.
/// </para>
/// </summary>
internal static class ExplicitKeyInsertRoundOrder
{
    /// <summary>
    /// Returns the round indexes in save order. Kahn's algorithm over the table dependency graph:
    /// a round becomes ready once every round holding one of its principal tables is ordered, and
    /// among ready rounds the lowest index goes first, so independent rounds keep their original
    /// order. A self-reference (a table pointing at itself) is no edge, since EF orders rows within
    /// one round itself. A cycle between rounds has no valid order; the lowest-index remaining round
    /// is taken to break it, which keeps the result deterministic.
    /// </summary>
    /// <param name="roundEntityTypes">The entity types each round inserts, by round index.</param>
    /// <returns>Every round index exactly once, principals before dependents.</returns>
    internal static IReadOnlyList<int> Order(IReadOnlyList<IReadOnlyCollection<IReadOnlyEntityType>> roundEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(roundEntityTypes);

        return TopologicalOrder(PrincipalRounds(roundEntityTypes));
    }

    /// <summary>For each round, the other rounds holding a table one of its foreign keys points at.</summary>
    private static HashSet<int>[] PrincipalRounds(IReadOnlyList<IReadOnlyCollection<IReadOnlyEntityType>> roundEntityTypes)
    {
        int count = roundEntityTypes.Count;

        // Table -> round, so a foreign key's principal type finds its round through the table it maps
        // to (several entity types can share a table, and the round is per table).
        var roundByTable = new Dictionary<(string? Schema, string? Table), int>();
        for (int round = 0; round < count; round++)
        {
            foreach (var entityType in roundEntityTypes[round])
            {
                roundByTable.TryAdd(TableOf(entityType), round);
            }
        }

        // principals[r] = the rounds round r depends on.
        var principals = new HashSet<int>[count];
        for (int round = 0; round < count; round++)
        {
            principals[round] = [];
            foreach (var foreignKey in roundEntityTypes[round].SelectMany(e => e.GetForeignKeys()))
            {
                if (roundByTable.TryGetValue(TableOf(foreignKey.PrincipalEntityType), out int principalRound)
                    && principalRound != round)
                {
                    principals[round].Add(principalRound);
                }
            }
        }

        return principals;
    }

    /// <summary>Kahn's algorithm, lowest ready index first, lowest remaining index to break a cycle.</summary>
    private static List<int> TopologicalOrder(HashSet<int>[] principals)
    {
        int count = principals.Length;
        var ordered = new List<int>(count);
        var placed = new bool[count];
        while (ordered.Count < count)
        {
            int next = -1;
            for (int round = 0; round < count; round++)
            {
                if (!placed[round] && principals[round].All(p => placed[p]))
                {
                    next = round;
                    break;
                }
            }

            if (next < 0)
            {
                // A cycle: no remaining round has all its principals placed.
                next = Array.IndexOf(placed, false);
            }

            placed[next] = true;
            ordered.Add(next);
        }

        return ordered;
    }

    private static (string? Schema, string? Table) TableOf(IReadOnlyEntityType entityType) =>
        (entityType.GetSchema(), entityType.GetTableName());
}
