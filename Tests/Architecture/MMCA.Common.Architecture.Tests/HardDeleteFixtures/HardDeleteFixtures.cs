using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Architecture.Tests.HardDeleteFixtures;

/// <summary>
/// Compiled call sites for <c>SoftDeleteEnforcementFitnessTests</c>. The hard-delete rule reads IL,
/// so the only honest way to test it is to compile the calls it must (and must not) flag into this
/// assembly and point a map at it.
/// </summary>
public sealed class FixtureEntity : AuditableBaseEntity<int>
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Erases rows through the entity set. The rule must flag both members.</summary>
internal static class DbSetRemovingFixture
{
    public static void Purge(DbSet<FixtureEntity> set, FixtureEntity entity) => set.Remove(entity);

    public static void PurgeMany(DbSet<FixtureEntity> set, IEnumerable<FixtureEntity> entities) =>
        set.RemoveRange(entities);
}

/// <summary>Erases rows with a server-side delete. The rule must flag it.</summary>
internal static class ExecuteDeletingFixture
{
    public static Task<int> PurgeAsync(IQueryable<FixtureEntity> query, CancellationToken cancellationToken) =>
        query.ExecuteDeleteAsync(cancellationToken);
}

/// <summary>
/// Deletes the framework's way, and removes from an ordinary in-memory collection. Neither is a
/// hard delete, so the rule must stay silent about this type: <c>Remove</c> on a <c>List&lt;T&gt;</c>
/// is what makes the naive "ban the name Remove" version of this rule useless.
/// </summary>
internal static class SoftDeletingFixture
{
    public static void Deactivate(FixtureEntity entity) => _ = entity.Delete();

    public static void Forget(List<FixtureEntity> cache, FixtureEntity entity) => _ = cache.Remove(entity);
}
