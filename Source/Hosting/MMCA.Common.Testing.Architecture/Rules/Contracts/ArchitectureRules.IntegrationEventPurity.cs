namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Every concrete integration event is declared in an assembly whose name ends with
    /// <c>.Shared</c> (ADR-010): an integration event is the module's PUBLIC contract, and a
    /// consuming module (or a consuming service) may only take a reference on Shared.
    /// </summary>
    /// <remarks>
    /// Vacuous for a module-less map. MMCA.Common is the framework, not a module: its one shipped
    /// event lives beside the envelope it inherits, in MMCA.Common.Domain, and every consumer sees
    /// it through the framework's public API baseline rather than through a module boundary. The
    /// sibling namespace rule (<see cref="IntegrationEventsResideInSharedIntegrationEventsNamespace"/>)
    /// scopes its Shared-layer half the same way, for the same reason.
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    public static void IntegrationEventsLiveInSharedAssemblies(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (map.ModuleNames.Count == 0)
        {
            return;
        }

        var violations = IntegrationEvents(map)
            .Where(t => !IsSharedAssembly(t.Assembly))
            .Select(t => $"  - {t.FullName} is declared in {t.Assembly.GetName().Name}, which is not a *.Shared assembly");

        ArchitectureAssert.NoViolations(violations,
            "an integration event is the module's public contract and a consumer may only reference Shared, "
            + "so the event type itself must ship from a *.Shared assembly (ADR-010)");
    }

    /// <summary>
    /// No integration event exposes a domain type on its wire shape (ADR-010): not directly, and not
    /// through a nested payload record declared in the same repo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Integration events are the public API; domain events are not. A property typed as an entity,
    /// a domain value object or a domain enumeration publishes the producer's internal model to
    /// every consumer, which turns an internal refactor into a cross-service breaking change and
    /// leaves fields on the wire that the producer never meant to share.
    /// </para>
    /// <para>
    /// The walk recurses through payload types the repo itself declares (a nested record shipped
    /// alongside the event), because burying an entity one level down hides the leak without
    /// removing it. It stops at framework and BCL types, which are not the repo's to police, and
    /// unwraps arrays, nullables and generic collection arguments so
    /// <c>IReadOnlyList&lt;OrderLine&gt;</c> is judged on <c>OrderLine</c>.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    public static void IntegrationEventPayloadsAreDomainFree(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = new List<string>();

        foreach (var eventType in IntegrationEvents(map))
        {
            var visited = new HashSet<Type>();

            foreach (var (path, leakedType) in DomainTypesReachableFrom(eventType, eventType.Name, visited))
            {
                violations.Add(
                    $"  - {eventType.FullName}: {path} is typed {leakedType.Name}, declared in the domain assembly "
                    + $"{leakedType.Assembly.GetName().Name}");
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "integration events are the public cross-service contract and domain types are not public API: "
            + "publishing an entity or a domain value object turns an internal refactor into a breaking change "
            + "for every consumer (ADR-010). Project the value onto a primitive or a contract record in Shared");
    }

    /// <summary>
    /// Walks an event's public properties, reporting every reachable type declared in a
    /// <c>*.Domain</c> assembly. Recursion is bounded by <paramref name="visited"/>, so a payload
    /// record that refers back to itself terminates.
    /// </summary>
    /// <param name="type">The type whose properties to walk.</param>
    /// <param name="path">The property path walked so far, used in the message.</param>
    /// <param name="visited">The types already walked on this branch.</param>
    /// <returns>One entry per reachable domain type, with the path that reaches it.</returns>
    private static IEnumerable<(string Path, Type LeakedType)> DomainTypesReachableFrom(
        Type type,
        string path,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var candidate in UnwrapTypeArguments(property.PropertyType))
            {
                var propertyPath = $"{path}.{property.Name}";

                if (IsDomainAssembly(candidate.Assembly))
                {
                    yield return (propertyPath, candidate);
                    continue;
                }

                // Only types this repo declares are walked further: a BCL or framework type is not
                // the repo's to police, and walking it would recurse through the whole platform.
                if (!IsRepoDeclared(candidate, type))
                {
                    continue;
                }

                foreach (var nested in DomainTypesReachableFrom(candidate, propertyPath, visited))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Reduces a property type to the types worth judging: the element type of an array, the
    /// underlying type of a nullable, and every generic argument of a collection or wrapper, plus
    /// the type itself.
    /// </summary>
    /// <param name="type">The declared property type.</param>
    /// <returns>The candidate types.</returns>
    private static IEnumerable<Type> UnwrapTypeArguments(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType() is { } element ? UnwrapTypeArguments(element) : [];
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return UnwrapTypeArguments(underlying);
        }

        return type.IsGenericType
            ? [type, .. type.GetGenericArguments().SelectMany(UnwrapTypeArguments)]
            : [type];
    }

    /// <summary>True when the assembly's simple name marks it as a domain assembly.</summary>
    private static bool IsDomainAssembly(Assembly assembly) =>
        assembly.GetName().Name?.Contains(".Domain", StringComparison.Ordinal) == true;

    /// <summary>True when the assembly's simple name marks it as a Shared (contract) assembly.</summary>
    private static bool IsSharedAssembly(Assembly assembly) =>
        assembly.GetName().Name?.EndsWith(".Shared", StringComparison.Ordinal) == true;

    /// <summary>
    /// True when a candidate payload type is declared by the same repo as the type that referenced
    /// it, and is a class or struct the walk can meaningfully recurse into.
    /// </summary>
    /// <param name="candidate">The candidate payload type.</param>
    /// <param name="declaringType">The type whose property produced the candidate.</param>
    /// <returns>Whether to recurse into the candidate.</returns>
    private static bool IsRepoDeclared(Type candidate, Type declaringType) =>
        !candidate.IsPrimitive
        && !candidate.IsEnum
        && candidate != typeof(string)
        && candidate.Assembly != typeof(string).Assembly
        && string.Equals(
            RepoPrefix(candidate.Assembly),
            RepoPrefix(declaringType.Assembly),
            StringComparison.Ordinal);

    /// <summary>The first two dot-separated segments of an assembly name, e.g. "MMCA.Store".</summary>
    private static string RepoPrefix(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? string.Empty;
        var segments = name.Split('.');
        return segments.Length >= 2 ? $"{segments[0]}.{segments[1]}" : name;
    }
}
