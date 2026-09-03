using System.Runtime.CompilerServices;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// No two event upcasters claim the same source contract (ADR-090). The upcast chain has to be a
    /// function: with two upcasters reading one type, which handler sees the message would depend on
    /// DI registration order.
    /// </summary>
    public static void EventUpcastersHaveUniqueSourceTypes(IArchitectureMap map)
    {
        var violations = EventUpcasters(map)
            .GroupBy(u => u.Source)
            .Where(g => g.Skip(1).Any())
            .Select(g => $"  - {Describe(g.Key)} is upcast by {string.Join(", ", g.Select(u => Describe(u.Upcaster)).Order(StringComparer.Ordinal))}: exactly one upcaster may claim a source contract");

        ArchitectureAssert.NoViolations(violations,
            "an integration event may have at most one upcaster reading it, otherwise the contract a handler receives depends on DI registration order (ADR-090)");
    }

    /// <summary>
    /// Every event upcaster moves FORWARD: the target contract declares a higher <c>SchemaVersion</c>
    /// than the source (ADR-010 plus ADR-090). A target at the same or a lower version means the new
    /// type is not actually a successor, and the chain can never be reasoned about as a version ladder.
    /// </summary>
    public static void EventUpcastersIncreaseSchemaVersion(IArchitectureMap map)
    {
        var violations = new List<string>();

        foreach (var (upcaster, source, target) in EventUpcasters(map))
        {
            var sourceVersion = SchemaVersionOf(source);
            var targetVersion = SchemaVersionOf(target);

            // A missing or non-int SchemaVersion is the business of
            // IntegrationEventsDeclareSchemaVersion; this rule only judges the ordering.
            if (sourceVersion is null || targetVersion is null)
            {
                continue;
            }

            if (targetVersion <= sourceVersion)
            {
                violations.Add(
                    $"  - {Describe(upcaster)} upcasts {source.Name} (SchemaVersion {sourceVersion}) to {target.Name} (SchemaVersion {targetVersion}): the target must declare a HIGHER SchemaVersion");
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "an upcaster converts a retired contract into its successor, so the target must declare a higher SchemaVersion than the source (ADR-010, ADR-090)");
    }

    /// <summary>
    /// Enumerates the event upcasters the map OWNS, scoped exactly like the integration events
    /// themselves: a module-bearing (consumer) map scans only module assemblies, while the framework's
    /// own module-less map scans every layer.
    /// </summary>
    private static IEnumerable<(Type Upcaster, Type Source, Type Target)> EventUpcasters(IArchitectureMap map)
    {
        var includeFrameworkLayers = map.ModuleNames.Count == 0;
        return map.Layers
            .Where(l => includeFrameworkLayers || l.Module.Length > 0)
            .Select(l => l.Assembly)
            .Distinct()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => !t.ContainsGenericParameters)
            .Select(t => (Upcaster: t, Contract: UpcasterContract(t)))
            .Where(x => x.Contract is not null)
            .Select(x => (
                x.Upcaster,
                Source: x.Contract!.GetGenericArguments()[0],
                Target: x.Contract!.GetGenericArguments()[1]));
    }

    /// <summary>
    /// Matches <c>IEventUpcaster&lt;TSource, TTarget&gt;</c> by name and arity, so the rule library
    /// keeps its no-compile-dependency idiom.
    /// </summary>
    private static Type? UpcasterContract(Type type) =>
        type.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && string.Equals(i.Name, "IEventUpcaster`2", StringComparison.Ordinal));

    /// <summary>
    /// Reads the declared <c>SchemaVersion</c> off an event contract without running a constructor:
    /// the property is a get-only virtual returning a literal, so an uninitialized instance answers
    /// correctly and no event needs a parameterless factory just to be inspected.
    /// </summary>
    private static int? SchemaVersionOf(Type eventType)
    {
        if (eventType.IsAbstract || eventType.IsInterface || eventType.ContainsGenericParameters)
        {
            return null;
        }

        var property = eventType.GetProperty("SchemaVersion", BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(int) || !property.CanRead)
        {
            return null;
        }

        return property.GetValue(RuntimeHelpers.GetUninitializedObject(eventType)) as int?;
    }

    private static string Describe(Type type) => type.FullName ?? type.Name;
}
