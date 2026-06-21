namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>Every concrete integration event declares an <c>int SchemaVersion</c> (ADR-010).</summary>
    public static void IntegrationEventsDeclareSchemaVersion(IArchitectureMap map)
    {
        var violations = IntegrationEvents(map)
            .Where(t => t.GetProperty("SchemaVersion", BindingFlags.Public | BindingFlags.Instance)?.PropertyType != typeof(int))
            .Select(t => $"  - {t.FullName} must declare an int SchemaVersion (supplied by BaseIntegrationEvent)");

        ArchitectureAssert.NoViolations(violations,
            "integration events must declare an int SchemaVersion so cross-service consumers have an explicit version signal (ADR-010)");
    }

    /// <summary>Every concrete integration event inherits <c>BaseIntegrationEvent</c> (the wire envelope).</summary>
    public static void IntegrationEventsInheritBaseIntegrationEvent(IArchitectureMap map)
    {
        var violations = IntegrationEvents(map)
            .Where(t => !t.HasBaseTypeStartingWith("MMCA.Common.Domain.DomainEvents.BaseIntegrationEvent"))
            .Select(t => $"  - {t.FullName} must inherit BaseIntegrationEvent (envelope + SchemaVersion)");

        ArchitectureAssert.NoViolations(violations,
            "integration events must inherit BaseIntegrationEvent so the cross-service envelope stays consistent");
    }

    /// <summary>Integration events live in a <c>*.IntegrationEvents</c> namespace within the Shared layer.</summary>
    public static void IntegrationEventsResideInSharedIntegrationEventsNamespace(IArchitectureMap map)
    {
        var sharedAssemblies = map.OfLayer(Layer.Shared).ToHashSet();
        var enforceSharedLayer = map.ModuleNames.Count > 0;
        var violations = IntegrationEvents(map)
            .Where(t => !IsInIntegrationEventsNamespace(t) || enforceSharedLayer && !sharedAssemblies.Contains(t.Assembly))
            .Select(t => $"  - {t.FullName} must live in a *.IntegrationEvents namespace in the Shared layer");

        ArchitectureAssert.NoViolations(violations,
            "integration events are cross-module contracts — they belong in the Shared layer's *.IntegrationEvents namespace");
    }

    /// <summary>
    /// Builds the frozen wire-contract snapshot: one line per integration event, declared public
    /// properties sorted by name, events sorted by full type name. Consumed by the per-repo
    /// IntegrationEventContractTestsBase, which compares it to a committed <c>ExpectedContract</c>.
    /// </summary>
    public static List<string> BuildIntegrationEventContract(IArchitectureMap map) =>
        [.. IntegrationEvents(map)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(DescribeIntegrationEvent)];

    private static string DescribeIntegrationEvent(Type eventType)
    {
        var properties = eventType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}:{p.PropertyType.Name}");

        return $"{eventType.FullName} {{ {string.Join(", ", properties)} }}";
    }

    private static IEnumerable<Type> IntegrationEvents(IArchitectureMap map) =>
        map.Layers.Select(l => l.Assembly).Distinct()
            .SelectMany(a => a.ConcreteClasses())
            .Where(IsIntegrationEvent);

    private static bool IsIntegrationEvent(Type type) =>
        type.GetInterfaces().Any(i => string.Equals(i.Name, "IIntegrationEvent", StringComparison.Ordinal));

    private static bool IsInIntegrationEventsNamespace(Type type) =>
        type.Namespace?.Contains(".IntegrationEvents", StringComparison.Ordinal) == true;
}
