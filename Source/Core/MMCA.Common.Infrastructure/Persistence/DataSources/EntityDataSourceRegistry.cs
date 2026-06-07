using System.Collections.Frozen;
using System.Reflection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;

namespace MMCA.Common.Infrastructure.Persistence.DataSources;

/// <summary>
/// Singleton implementation of <see cref="IEntityDataSourceRegistry"/>. Reflects over the
/// configuration assemblies from <see cref="IEntityConfigurationAssemblyProvider"/>, derives each
/// entity's physical data source from its configuration class
/// (engine via <see cref="UseDataSourceAttribute"/>, logical name via
/// <see cref="UseDatabaseAttribute"/> → module namespace → <c>Default</c>), and resolves the
/// logical name through <see cref="IDataSourceResolver"/>.
/// <para>
/// Built lazily on first access and rescanned once on a lookup miss to pick up module assemblies
/// loaded after the first access. Duplicate registrations of one entity are tolerated when they
/// agree on the physical source and rejected (fail-fast) when they conflict.
/// </para>
/// </summary>
public sealed class EntityDataSourceRegistry(
    IEntityConfigurationAssemblyProvider assemblyProvider,
    IDataSourceResolver resolver) : IEntityDataSourceRegistry
{
    private sealed record Snapshot(
        FrozenDictionary<string, (DataSourceKey Key, Type ConfigurationType)> Entities,
        FrozenSet<Assembly> ScannedAssemblies);

    private readonly Lock _rebuildLock = new();
    private volatile Snapshot? _snapshot;

    /// <inheritdoc />
    public DataSourceKey GetDataSourceKey(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return GetDataSourceKey(entityType.FullName!);
    }

    /// <inheritdoc />
    public DataSourceKey GetDataSourceKey(string entityFullName) =>
        TryGetDataSourceKey(entityFullName, out var key)
            ? key
            : throw new InvalidOperationException(
                $"DataSource not defined for {entityFullName}. " +
                "Ensure an entity type configuration (EntityTypeConfigurationSQLServer/Cosmos/Sqlite) exists " +
                "for the entity in a discovered configuration assembly.");

    /// <inheritdoc />
    public bool TryGetDataSourceKey(string entityFullName, out DataSourceKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityFullName);

        var snapshot = GetOrBuildSnapshot();
        if (snapshot.Entities.TryGetValue(entityFullName, out var registration))
        {
            key = registration.Key;
            return true;
        }

        // Miss: a module assembly may have been loaded after the first scan. Rescan once when the
        // assembly set changed, then retry.
        snapshot = RescanIfAssembliesChanged(snapshot);
        if (snapshot.Entities.TryGetValue(entityFullName, out registration))
        {
            key = registration.Key;
            return true;
        }

        key = default;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DataSourceKey> GetPhysicalSourcesInUse() =>
        [.. GetOrBuildSnapshot().Entities.Values.Select(r => r.Key).Distinct()];

    private Snapshot GetOrBuildSnapshot()
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_rebuildLock)
        {
            return _snapshot ??= BuildSnapshot();
        }
    }

    private Snapshot RescanIfAssembliesChanged(Snapshot current)
    {
        lock (_rebuildLock)
        {
            var snapshot = _snapshot ?? current;
            var assemblies = assemblyProvider.GetConfigurationAssemblies();
            if (assemblies.All(snapshot.ScannedAssemblies.Contains))
            {
                return snapshot;
            }

            snapshot = BuildSnapshot();
            _snapshot = snapshot;
            return snapshot;
        }
    }

    private Snapshot BuildSnapshot()
    {
        var assemblies = assemblyProvider.GetConfigurationAssemblies().Distinct().ToList();
        var entities = new Dictionary<string, (DataSourceKey Key, Type ConfigurationType)>(StringComparer.Ordinal);

        foreach (var configurationType in assemblies.SelectMany(GetLoadableTypes))
        {
            if (configurationType.IsAbstract || configurationType.IsGenericTypeDefinition)
            {
                continue;
            }

            var baseInterface = configurationType.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfigurationBase<,>));
            if (baseInterface is null)
            {
                continue;
            }

            var entityType = baseInterface.GenericTypeArguments[0];
            var key = DeriveDataSourceKey(configurationType, entityType);
            if (key is null)
            {
                continue;
            }

            if (entities.TryGetValue(entityType.FullName!, out var existing))
            {
                if (existing.Key != key.Value)
                {
                    throw new InvalidOperationException(
                        $"Entity \"{entityType.FullName}\" is registered by multiple configurations targeting " +
                        $"different data sources: \"{existing.ConfigurationType.FullName}\" → {existing.Key}, " +
                        $"\"{configurationType.FullName}\" → {key}. An entity must live in exactly one database.");
                }

                continue;
            }

            entities[entityType.FullName!] = (key.Value, configurationType);
        }

        return new Snapshot(
            entities.ToFrozenDictionary(StringComparer.Ordinal),
            assemblies.ToFrozenSet());
    }

    /// <summary>
    /// Derives the physical data source for a configuration class:
    /// engine from <see cref="UseDataSourceAttribute"/> (declared on the provider-specific base class),
    /// logical name from <see cref="UseDatabaseAttribute"/> → entity module namespace → Default,
    /// then collapsed to a physical key by the resolver.
    /// Configurations without <see cref="UseDataSourceAttribute"/> (implementing a provider
    /// interface directly instead of deriving from the attributed base classes) are skipped —
    /// their entities are not routable through the unit of work, matching legacy behavior.
    /// </summary>
    private DataSourceKey? DeriveDataSourceKey(Type configurationType, Type entityType)
    {
        var engine = configurationType.GetCustomAttribute<UseDataSourceAttribute>()?.DataSource;
        if (engine is null)
        {
            return null;
        }

        var logicalName = configurationType.GetCustomAttribute<UseDatabaseAttribute>()?.Name
            ?? NamespaceConventions.GetModuleName(entityType)
            ?? DataSourceKey.DefaultName;

        return resolver.ResolveLogical(engine.Value, logicalName);
    }

    /// <summary>
    /// Gets the types of an assembly, tolerating partial load failures (mirrors module discovery).
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
