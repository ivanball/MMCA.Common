namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The non-generic cache-invalidation marker the caching command decorator reads.</summary>
    private const string CacheInvalidatingFullName = "MMCA.Common.Application.UseCases.Markers.ICacheInvalidating";

    /// <summary>The entity-keyed marker that supplies the prefix as a default interface member.</summary>
    private const string EntityKeyedCacheInvalidatingFullName = "MMCA.Common.Application.UseCases.Markers.ICacheInvalidating`1";

    /// <summary>The marker's prefix member, declared or implemented explicitly.</summary>
    private const string CachePrefixMemberName = "CachePrefix";

    /// <summary>
    /// Every concrete <c>ICacheInvalidating</c> implementer in the mapped Application assemblies
    /// (framework and module) implements <c>ICacheInvalidating&lt;TEntity&gt;</c> and declares no
    /// <c>CachePrefix</c> of its own, or is exempt through <paramref name="allowedCustomPrefixTypes"/>.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedCustomPrefixTypes">
    /// Types with a deliberately custom prefix. An open generic definition exempts its closed forms,
    /// and a type deriving from an allow-listed type is exempt too.
    /// </param>
    /// <remarks>
    /// <para>
    /// A class member that implements <c>CachePrefix</c> wins over the generic marker's default, so a
    /// type that names its entity and then declares a prefix anyway (on itself or on a base class it
    /// inherits from) evicts the hand-written string, not the entity prefix. Both that shape and a
    /// prefix on the non-generic marker are reported; abstract types are skipped, because only a type
    /// that can be sent through the pipeline carries a prefix at runtime.
    /// </para>
    /// <para>
    /// Detection is by interface full name, keeping this package free of a framework reference exactly
    /// as the rest of the rule library is. Non-public types are scanned too.
    /// </para>
    /// </remarks>
    public static void CacheInvalidatingCommandsUseEntityKeyedMarker(
        IArchitectureMap map,
        IReadOnlyCollection<Type> allowedCustomPrefixTypes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedCustomPrefixTypes);

        var violations = map.OfLayer(Layer.Application)
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => !IsCompilerGenerated(t))
            .Where(t => ImplementsInterfaceNamed(t, CacheInvalidatingFullName))
            .Where(t => !IsAllowListed(t, allowedCustomPrefixTypes))
            .Select(DescribeCachePrefixViolation)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(
            violations,
            "every ICacheInvalidating command must implement ICacheInvalidating<TEntity> and declare no "
            + "CachePrefix of its own, so it evicts the \"{typeof(TEntity).FullName}:\" key space its "
            + "aggregate's cached reads live under; a deliberately custom prefix goes on the non-generic "
            + "marker and into AllowedCustomPrefixTypes");
    }

    /// <summary>The violation line for one implementer, or null when it follows the convention.</summary>
    private static string? DescribeCachePrefixViolation(Type type)
    {
        if (!ImplementsInterfaceNamed(type, EntityKeyedCacheInvalidatingFullName))
        {
            return $"  - {type.FullName} implements ICacheInvalidating without ICacheInvalidating<TEntity>: "
                + "name the entity through the generic marker and delete the hand-written prefix, or allow-list the type";
        }

        var declaringType = CachePrefixDeclaringType(type);

        return declaringType is null
            ? null
            : $"  - {type.FullName} implements ICacheInvalidating<TEntity> but {declaringType.FullName} declares "
                + "its own CachePrefix, which replaces the entity prefix: delete it, or move the type to the "
                + "non-generic marker and allow-list it";
    }

    /// <summary>
    /// The first class in <paramref name="type"/>'s hierarchy that declares a <c>CachePrefix</c>
    /// property (implicitly or as an explicit interface implementation), or null when none does.
    /// </summary>
    private static Type? CachePrefixDeclaringType(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            var declaresPrefix = current.GetProperties(declared)
                .Any(p => string.Equals(p.Name, CachePrefixMemberName, StringComparison.Ordinal)
                    || p.Name.EndsWith("." + CachePrefixMemberName, StringComparison.Ordinal));

            if (declaresPrefix)
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="type"/> or any base class is allow-listed, directly or through its open
    /// generic definition.
    /// </summary>
    private static bool IsAllowListed(Type type, IReadOnlyCollection<Type> allowed)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (allowed.Contains(current)
                || current.IsGenericType && allowed.Contains(current.GetGenericTypeDefinition()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the type implements an interface of the given full name, comparing an open or closed
    /// generic interface by its generic definition (<c>Name`1</c>).
    /// </summary>
    private static bool ImplementsInterfaceNamed(Type type, string interfaceFullName) =>
        type.GetInterfaces().Any(i => string.Equals(
            i.IsGenericType ? i.GetGenericTypeDefinition().FullName : i.FullName,
            interfaceFullName,
            StringComparison.Ordinal));
}
