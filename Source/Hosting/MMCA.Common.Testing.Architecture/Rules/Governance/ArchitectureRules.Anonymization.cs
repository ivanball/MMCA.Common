namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Every concrete <c>IAnonymizable</c> class among <paramref name="candidateTypes"/> has a sample in
    /// <paramref name="samples"/>, so <see cref="PropertiesOverwrittenByAnonymizeCarryPii"/> exercises
    /// its erasure (ADR-005). Without this, a new anonymizable entity silently escapes the reverse rule.
    /// </summary>
    /// <param name="candidateTypes">The types to scan, normally every type of the module Domain assemblies.</param>
    /// <param name="samples">The sample table: entity type to a factory building a populated instance.</param>
    public static void AnonymizableEntitiesAreCoveredBySamples(
        IEnumerable<Type> candidateTypes,
        IReadOnlyDictionary<Type, Func<object>> samples)
    {
        ArgumentNullException.ThrowIfNull(candidateTypes);
        ArgumentNullException.ThrowIfNull(samples);

        var violations = candidateTypes
            .Where(t => t is { IsClass: true, IsAbstract: false } && FindAnonymizable(t) is not null)
            .Where(t => !samples.ContainsKey(t))
            .Select(t => t.FullName ?? t.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => $"  - {name}");

        ArchitectureAssert.NoViolations(violations,
            "every IAnonymizable domain entity needs a sample so its Anonymize() is checked against the [Pii] markers (ADR-005). "
            + "Override AnonymizableSamples in your PiiConventionTestsBase subclass and add [typeof(Entity)] = static () => <factory call that builds a populated instance> for each entity below");
    }

    /// <summary>
    /// The reverse of <see cref="EntitiesWithPiiImplementAnonymizable"/>, checked behaviorally: each
    /// sample is built, every text or reference property DECLARED on the sample's type is snapshotted,
    /// <c>Anonymize()</c> runs, and every such property whose value moved must carry <c>[Pii]</c>
    /// (matched by name). A property the erasure overwrites is personal data by construction; without
    /// the marker the audit trail keeps its pre-erasure value in <c>OldValue</c> for the whole retention
    /// window (ADR-005, ADR-075).
    /// </summary>
    /// <remarks>
    /// Value types are out of scope: an erasure legitimately flips non-personal state alongside the
    /// personal data (an <c>IsActive</c> flag going false), and <c>[Pii]</c> on a boolean would say
    /// something untrue about the row. Collections are out too: they hold credential buffers or child
    /// entities that carry their own markers, and a getter wrapping a backing list hands back a fresh
    /// wrapper on every read, so it never compares equal to its own snapshot. <c>DeclaredOnly</c> keeps
    /// framework base-class state (audit stamps, the domain-event collection) out of the comparison.
    /// </remarks>
    /// <param name="samples">The sample table: entity type to a factory building a populated instance.</param>
    public static void PropertiesOverwrittenByAnonymizeCarryPii(IReadOnlyDictionary<Type, Func<object>> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        List<string> violations = [];
        foreach (var (entityType, build) in samples.OrderBy(s => s.Key.FullName, StringComparer.Ordinal))
        {
            violations.AddRange(OverwrittenUnmarkedProperties(entityType, build));
        }

        ArchitectureAssert.NoViolations(violations,
            "a property that Anonymize() overwrites is personal data by construction, so it must be marked [Pii], "
            + "or the audit trail keeps its pre-erasure value in OldValue for the whole retention window (ADR-005, ADR-075). "
            + "Mark each property below [Pii] (and redact its existing audit rows), or stop overwriting it in Anonymize() if it is not personal data");
    }

    private static IEnumerable<string> OverwrittenUnmarkedProperties(Type entityType, Func<object> build)
    {
        var anonymize = FindAnonymizable(entityType)?.GetMethod("Anonymize", Type.EmptyTypes);
        if (anonymize is null)
        {
            return [$"  - {entityType.Name}: the sample key does not implement {AnonymizableFullName}; remove it from AnonymizableSamples"];
        }

        var entity = build();
        if (!entityType.IsInstanceOfType(entity))
        {
            return [$"  - {entityType.Name}: the sample factory returned {entity?.GetType().Name ?? "null"}, not an instance of {entityType.Name}"];
        }

        var properties = entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && CarriesPersonalData(p))
            .ToList();

        var before = properties.ToDictionary(p => p, p => p.GetValue(entity));

        var result = anonymize.Invoke(entity, null);
        if (result?.GetType().GetProperty("IsSuccess")?.GetValue(result) is not true)
        {
            return [$"  - {entityType.Name}: Anonymize() did not succeed on its sample; the sample must be a valid, populated instance"];
        }

        return [.. properties
            .Where(p => !Equals(before[p], p.GetValue(entity)))
            .Where(p => !p.GetCustomAttributes(inherit: true).Any(a => string.Equals(a.GetType().Name, "PiiAttribute", StringComparison.Ordinal)))
            .Select(p => $"  - {entityType.Name}.{p.Name}")
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether a property is the KIND of state the reverse rule governs: free text and the value
    /// objects built out of it, which is what the audit trail would otherwise keep verbatim.
    /// </summary>
    private static bool CarriesPersonalData(PropertyInfo property)
    {
        var propertyType = property.PropertyType;

        if (propertyType == typeof(string))
        {
            return true;
        }

        return !propertyType.IsValueType
            && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType);
    }

    private static Type? FindAnonymizable(Type type) =>
        type.GetInterfaces().FirstOrDefault(i => string.Equals(i.FullName, AnonymizableFullName, StringComparison.Ordinal));
}
