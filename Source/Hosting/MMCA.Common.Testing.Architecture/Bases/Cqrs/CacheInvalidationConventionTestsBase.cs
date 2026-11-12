namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Cache-invalidation convention gate: a command that evicts cached reads on success names the
/// aggregate it makes stale through <c>ICacheInvalidating&lt;TEntity&gt;</c> and lets the framework
/// supply the prefix (<c>"{typeof(TEntity).FullName}:"</c>), so the key space a command evicts is the
/// one the aggregate's cached reads actually live under. A hand-written prefix is a string that can
/// drift from the reads it is meant to evict, and nothing else notices when it does.
/// <para>
/// Every concrete type in the mapped Application assemblies (framework and module layers) that
/// implements <c>ICacheInvalidating</c> must either implement <c>ICacheInvalidating&lt;TEntity&gt;</c>
/// and declare no <c>CachePrefix</c> of its own, or be named in <see cref="AllowedCustomPrefixTypes"/>.
/// </para>
/// <para>
/// Adoption: subclass and supply <see cref="Map"/>. Move each command onto the generic marker and
/// delete its prefix, and allow-list the ones whose prefix is deliberately custom (a module-wide
/// prefix, a per-instance opt-out).
/// </para>
/// </summary>
public abstract class CacheInvalidationConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Types whose cache prefix is deliberately custom. An open generic definition exempts all of its
    /// closed forms, and a type deriving from an allow-listed type is exempt too, so a consumer's
    /// subclass of a framework command inherits the framework's exemption. Empty by default, which
    /// requires every invalidating command to use the entity-keyed marker.
    /// </summary>
    protected virtual IReadOnlyCollection<Type> AllowedCustomPrefixTypes => [];

    [Fact]
    public void CacheInvalidatingCommands_ShouldUse_EntityKeyedMarker() =>
        ArchitectureRules.CacheInvalidatingCommandsUseEntityKeyedMarker(Map, AllowedCustomPrefixTypes);
}
