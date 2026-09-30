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
    /// Builds the frozen wire-contract snapshot: one line per integration event, its public
    /// instance properties below the framework envelope (an intermediate consumer base is
    /// included; <c>DateOccurred</c>, <c>MessageId</c> and <c>SchemaVersion</c> are not) sorted by
    /// name with generic arguments spelled out (<c>Nullable&lt;Int32&gt;</c>), events sorted by
    /// full type name. Consumed by the per-repo IntegrationEventContractTestsBase, which compares
    /// it to a committed <c>ExpectedContract</c>.
    /// </summary>
    public static List<string> BuildIntegrationEventContract(IArchitectureMap map) =>
        [.. IntegrationEvents(map)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(DescribeIntegrationEvent)];

    private const string FrameworkEnvelopeNamespace = "MMCA.Common.Domain.DomainEvents";

    private static string DescribeIntegrationEvent(Type eventType)
    {
        var properties = eventType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !string.Equals(p.DeclaringType?.Namespace, FrameworkEnvelopeNamespace, StringComparison.Ordinal))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.MaxBy(p => InheritanceDepth(p.DeclaringType))!)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}:{ContractTypeName(p.PropertyType)}");

        return $"{eventType.FullName} {{ {string.Join(", ", properties)} }}";
    }

    /// <summary>
    /// The wire-shape name of a property type: the simple name, with generic arguments spelled out
    /// recursively and arrays suffixed, so a retyped generic argument changes the line. Namespaces
    /// stay out because the wire carries none.
    /// </summary>
    private static string ContractTypeName(Type type)
    {
        if (type.IsArray)
        {
            return ContractTypeName(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var name = tick < 0 ? type.Name : type.Name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(ContractTypeName))}>";
    }

    /// <summary>How far below <see cref="object"/> a type sits; the most-derived re-declaration wins.</summary>
    private static int InheritanceDepth(Type? type)
    {
        var depth = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>
    /// Enumerates the integration events the map OWNS. In a module-bearing (consumer) map only
    /// module assemblies are scanned: events shipped by the framework itself (for example
    /// <c>OutputCacheEvictionRequested</c>) are the framework's contract, gated by the framework's
    /// own conventions and public API baseline, so consumer residency rules and frozen snapshots
    /// neither police nor churn on them. The framework's own map has no modules and therefore
    /// scans every layer, keeping those same events covered at their source.
    /// </summary>
    private static IEnumerable<Type> IntegrationEvents(IArchitectureMap map)
    {
        var includeFrameworkLayers = map.ModuleNames.Count == 0;
        return map.Layers
            .Where(l => includeFrameworkLayers || l.Module.Length > 0)
            .Select(l => l.Assembly)
            .Distinct()
            .SelectMany(a => a.ConcreteClasses)
            .Where(IsIntegrationEvent);
    }

    private static bool IsIntegrationEvent(Type type) =>
        type.GetInterfaces().Any(i => string.Equals(i.Name, "IIntegrationEvent", StringComparison.Ordinal));

    private static bool IsInIntegrationEventsNamespace(Type type) =>
        type.Namespace?.Contains(".IntegrationEvents", StringComparison.Ordinal) == true;
}
