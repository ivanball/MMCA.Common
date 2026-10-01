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
    /// Builds the frozen wire-contract snapshot: one line per integration event, keyed by its
    /// <c>[EventName]</c> wire identity when it declares one and by its full type name otherwise,
    /// listing its public instance properties below the framework envelope (an intermediate
    /// consumer base is included; <c>DateOccurred</c>, <c>MessageId</c> and <c>SchemaVersion</c> are
    /// not) sorted by name, with generic arguments spelled out (<c>Nullable&lt;Int32&gt;</c>) and a
    /// nullable reference annotation marked (<c>String?</c>, <c>IReadOnlyList&lt;String?&gt;</c>),
    /// lines sorted by key. Consumed by the per-repo IntegrationEventContractTestsBase, which
    /// compares it to a committed <c>ExpectedContract</c>.
    /// </summary>
    public static List<string> BuildIntegrationEventContract(IArchitectureMap map)
    {
        // NullabilityInfoContext caches per instance and is not thread-safe: one per build.
        var nullability = new NullabilityInfoContext();
        return [.. IntegrationEvents(map)
            .Select(t => DescribeIntegrationEvent(t, nullability))
            .Order(StringComparer.Ordinal)];
    }

    private const string FrameworkEnvelopeNamespace = "MMCA.Common.Domain.DomainEvents";

    /// <summary>Matched by name: this package takes no reference on MMCA.Common.Domain.</summary>
    private const string EventNameAttributeFullName = "MMCA.Common.Domain.Attributes.EventNameAttribute";

    private static string DescribeIntegrationEvent(Type eventType, NullabilityInfoContext nullability)
    {
        var properties = eventType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !string.Equals(p.DeclaringType?.Namespace, FrameworkEnvelopeNamespace, StringComparison.Ordinal))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.MaxBy(p => InheritanceDepth(p.DeclaringType))!)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}:{ContractTypeName(p.PropertyType, nullability.Create(p))}");

        return $"{ContractKey(eventType)} {{ {string.Join(", ", properties)} }}";
    }

    /// <summary>
    /// The event's wire identity: the <c>[EventName]</c> value the outbox and inbox store when the
    /// event declares one (so a CLR rename or namespace move does not churn the snapshot, exactly as
    /// it does not churn the wire), otherwise the full type name the outbox falls back to.
    /// </summary>
    private static string ContractKey(Type eventType)
    {
        var declared = eventType.GetCustomAttributesData()
            .FirstOrDefault(a => string.Equals(a.AttributeType.FullName, EventNameAttributeFullName, StringComparison.Ordinal));
        return declared?.ConstructorArguments.Count == 1 && declared.ConstructorArguments[0].Value is string name
            ? name
            : eventType.FullName!;
    }

    /// <summary>
    /// The wire-shape name of a property type: the simple name, with generic arguments spelled out
    /// recursively, arrays suffixed and a nullable reference annotation marked with <c>?</c>, so a
    /// retyped generic argument or a member that may now be null changes the line. A nullable value
    /// type keeps its <c>Nullable&lt;T&gt;</c> spelling. Namespaces stay out because the wire carries
    /// none.
    /// </summary>
    private static string ContractTypeName(Type type, NullabilityInfo? info)
    {
        var marker = !type.IsValueType && info?.ReadState == NullabilityState.Nullable ? "?" : string.Empty;

        if (type.IsArray)
        {
            return ContractTypeName(type.GetElementType()!, info?.ElementType) + "[]" + marker;
        }

        if (!type.IsGenericType)
        {
            return type.Name + marker;
        }

        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var name = tick < 0 ? type.Name : type.Name[..tick];
        var arguments = type.GetGenericArguments()
            .Select((argument, i) => ContractTypeName(argument, info is not null && i < info.GenericTypeArguments.Length ? info.GenericTypeArguments[i] : null));
        return $"{name}<{string.Join(", ", arguments)}>{marker}";
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
